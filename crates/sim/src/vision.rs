//! Обзор игроков (vision.ts): что игрок видит сейчас — слой игрока `sight`, — и что разведал — слой `explored`
//! (docs/layers.md, С5). Считается по его и союзным юнитам, зданиям, вспышкам радаров и всем вспышкам ядерных
//! взрывов. Как у хоста в TS (`strict`): новый игрок считается при первом вопросе, дальше обзор пересчитывает только
//! система `vision` — раз в VISION_TICKS тиков.

use crate::data::DATA;
use crate::values::{number, text};
use kharos_engine::{LayerId, Layers, Owner, World};
use std::collections::{HashMap, HashSet};

/// Насколько обзор дальше самого дальнобойного оружия.
const SIGHT_OVER_RANGE: f64 = 1.5;
/// Сколько тайлов от края видит готовое здание и стройка, если у вида нет своего числа.
const BUILDING_SIGHT: f64 = 4.0;
const SITE_SIGHT: f64 = 2.0;
/// Вспышка разведки радара: радиус и сколько тиков она светит.
const SCAN_SIZE: f64 = 6.0;
const SCAN_TICKS: i64 = 30;
/// Раз во сколько тиков пересчитывается обзор игрока.
const VISION_TICKS: i64 = 4;

/// Сколько тайлов от своего центра видит юнит каждого класса, если у вида нет своего числа.
fn class_sight(kind: &str) -> f64 {
  match kind {
    "infantry" => 7.0,
    "air" => 10.0,
    _ => 8.0,
  }
}

/// Дальность самого дальнобойного оружия — своего или на турелях (`longestRange`).
fn longest_range(weapon: Option<&str>, mounts: &[crate::data::Mount]) -> f64 {
  let mut range = weapon.and_then(|weapon| DATA.weapons.get(weapon)).map_or(0.0, |spec| spec.range);
  for mount in mounts {
    let own = DATA.turrets.get(&mount.turret).and_then(|turret| turret.weapon.as_deref());
    if let Some(spec) = own.and_then(|weapon| DATA.weapons.get(weapon)) {
      range = range.max(spec.range);
    }
  }
  range
}

/// Сколько тайлов от своего центра видит юнит вида kind (`unitSight`).
pub fn unit_sight(kind: &str) -> f64 {
  let Some(spec) = DATA.units.get(kind) else { return 0.0 };
  let range = longest_range(spec.weapon.as_deref(), &spec.mounts);
  spec.sight.unwrap_or_else(|| class_sight(&spec.kind)).max(if range != 0.0 { range + SIGHT_OVER_RANGE } else { 0.0 })
}

/// Сколько тайлов от своего центра видит здание вида kind; стройка видит меньше, выключенное — как обычное
/// (`buildingSight`).
pub fn building_sight(kind: &str, site: bool, off: bool) -> f64 {
  let Some(spec) = DATA.buildings.get(kind) else { return 0.0 };
  let half = spec.width.max(spec.height) / 2.0;
  if site {
    return half + SITE_SIGHT;
  }
  if off {
    return half + BUILDING_SIGHT;
  }
  let range = longest_range(None, &spec.mounts);
  spec.sight.unwrap_or(half + BUILDING_SIGHT).max(if range != 0.0 { range + SIGHT_OVER_RANGE } else { 0.0 })
}

/// Прямоугольник карты в тайлах: [left, right) × [top, bottom).
#[derive(Clone, Copy, Debug)]
pub struct Bounds {
  pub left: i32,
  pub top: i32,
  pub right: i32,
  pub bottom: i32,
}

impl Bounds {
  pub fn width(&self) -> i32 {
    self.right - self.left
  }

  pub fn height(&self) -> i32 {
    self.bottom - self.top
  }

  fn contains(&self, x: i32, y: i32) -> bool {
    x >= self.left && x < self.right && y >= self.top && y < self.bottom
  }
}

struct Sight {
  /// Тайлы, открытые при прошлом подсчёте, в порядке открытия: их при следующем гасят до «разведан».
  lit: Vec<(i32, i32)>,
  tick: i64,
  /// Растёт, когда открытое поменялось.
  changed: u32,
}

pub struct Vision {
  pub bounds: Bounds,
  /// Нет тумана — каждому видна вся карта, и считать нечего.
  pub fog: bool,
  sight: LayerId,
  explored: LayerId,
  /// Игроки в порядке появления их обзора — как ключи Map в TS.
  order: Vec<u32>,
  sights: HashMap<u32, Sight>,
}

/// Ключ ячейки или тайла одним числом — tileKey в TS.
pub fn tile_key(x: i32, y: i32) -> f64 {
  (y as f64 + 32768.0) * 65536.0 + x as f64 + 32768.0
}

impl Vision {
  pub fn new(layers: &Layers, bounds: Bounds, fog: bool) -> Self {
    let sight = layers.id("sight").expect("нет слоя sight");
    let explored = layers.id("explored").expect("нет слоя explored");
    Self { bounds, fog, sight, explored, order: Vec::new(), sights: HashMap::new() }
  }

  /// Сколько раз менялось открытое игроком: окно перерисовывает туман, когда число выросло.
  pub fn changed(&self, player: u32) -> u32 {
    self.sights.get(&player).map_or(0, |sight| sight.changed)
  }

  /// Игроки, у которых есть обзор, в порядке появления.
  pub fn players(&self) -> &[u32] {
    &self.order
  }

  /// Обзор игрока; settle — пересчитать, если пора. Новый игрок заводится и считается сразу (`sightOf`).
  pub fn open(&mut self, world: &World, layers: &mut Layers, player: u32, now: i64, mut settle: bool) {
    if !self.sights.contains_key(&player) {
      self.sights.insert(player, Sight { lit: Vec::new(), tick: -1, changed: 0 });
      self.order.push(player);
      layers.add_player(player);
      settle = true;
    }
    if !settle {
      return;
    }
    let (bounds, fog, sight_layer, explored_layer) = (self.bounds, self.fog, self.sight, self.explored);
    let sight = self.sights.get_mut(&player).unwrap();
    if !fog {
      sight.tick = now;
      return;
    }
    if sight.tick >= 0 && now >= sight.tick && now - sight.tick < VISION_TICKS {
      return;
    }
    sight.tick = now;
    let owner = Owner::Player(player);
    let before = std::mem::take(&mut sight.lit);
    for &(x, y) in &before {
      layers.set(sight_layer, owner, x, y, 0, 0.0);
    }
    let mut stamp = |x: f64, y: f64, radius: f64| {
      let reach = radius * radius;
      let from_y = bounds.top.max((y - radius).floor() as i32);
      let to_y = (bounds.bottom - 1).min((y + radius).floor() as i32);
      for tile_y in from_y..=to_y {
        let dy = tile_y as f64 + 0.5 - y;
        let span = (reach - dy * dy).max(0.0).sqrt();
        let from_x = bounds.left.max((x - span).floor() as i32);
        let to_x = (bounds.right - 1).min((x + span).floor() as i32);
        for tile_x in from_x..=to_x {
          if layers.get(sight_layer, owner, tile_x, tile_y, 0) != 0.0 {
            continue;
          }
          layers.set(sight_layer, owner, tile_x, tile_y, 0, 1.0);
          layers.set(explored_layer, owner, tile_x, tile_y, 0, 1.0);
          sight.lit.push((tile_x, tile_y));
        }
      }
    };
    // Разведка у союзников общая.
    let team = allies_in(world, player);
    let ours = |entity| number(world, entity, "Owner", "player").is_some_and(|other| other == player as f64 || team.contains(&other));
    for entity in world.query(&["Unit", "Owner", "Position"]) {
      if ours(entity) && !world.has(entity, "Attached") {
        let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
        stamp(x, y, unit_sight(text(world, entity, "Unit", "type").unwrap_or_default()));
      }
    }
    for entity in world.query(&["Building", "Owner", "Position"]) {
      if !ours(entity) || world.has(entity, "Attached") {
        continue;
      }
      let kind = text(world, entity, "Building", "type").unwrap_or_default();
      let Some(spec) = DATA.buildings.get(kind) else { continue };
      let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
      let dark = world.get(entity, "Scan").and_then(|scan| scan.get("dark")).and_then(|dark| dark.as_bool()).unwrap_or(false);
      stamp(x + spec.width / 2.0, y + spec.height / 2.0, building_sight(kind, world.has(entity, "Site"), world.has(entity, "Off") || dark));
    }
    // Вспышки разведки своих радаров, пока не погасли.
    for entity in world.query(&["Scan", "Owner"]) {
      let flash = |field| number(world, entity, "Scan", field).unwrap_or(0.0);
      if ours(entity) && flash("tick") >= 0.0 && (now as f64) - flash("tick") < SCAN_TICKS as f64 {
        stamp(flash("x"), flash("y"), SCAN_SIZE);
      }
    }
    // Вспышка ядерного взрыва открывает свой круг всем.
    for entity in world.query(&["Glare", "Position"]) {
      if (now as f64) < number(world, entity, "Glare", "until").unwrap_or(0.0) {
        let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
        stamp(x, y, number(world, entity, "Glare", "radius").unwrap_or(0.0));
      }
    }
    if before != sight.lit {
      sight.changed += 1;
    }
  }

  /// Система `vision`: пересчитывает обзор всех, у кого что-то есть на карте, и всех, у кого обзор уже есть (`update`).
  pub fn update(&mut self, world: &World, layers: &mut Layers, now: i64) {
    let mut players = Vec::new();
    let mut seen = HashSet::new();
    for entity in world.query(&["Owner", "Position"]) {
      let player = number(world, entity, "Owner", "player").unwrap_or(0.0);
      if player != 0.0 && seen.insert(player as u32) {
        players.push(player as u32);
      }
    }
    for &player in &self.order {
      if seen.insert(player) {
        players.push(player);
      }
    }
    for player in players {
      self.open(world, layers, player, now, true);
    }
  }

  fn tile(x: f64, y: f64) -> (i32, i32) {
    (x.floor() as i32, y.floor() as i32)
  }

  /// Видит ли игрок тайл сейчас. Обзор игрока должен быть заведён (open).
  pub fn sees(&self, layers: &Layers, player: u32, x: f64, y: f64) -> bool {
    let (x, y) = Self::tile(x, y);
    self.bounds.contains(x, y) && (!self.fog || layers.get(self.sight, Owner::Player(player), x, y, 0) != 0.0)
  }

  /// Бывал ли тайл в виду игрока.
  pub fn explored(&self, layers: &Layers, player: u32, x: f64, y: f64) -> bool {
    let (x, y) = Self::tile(x, y);
    self.bounds.contains(x, y) && (!self.fog || layers.get(self.explored, Owner::Player(player), x, y, 0) != 0.0)
  }

  /// Тайлы карты построчно: 0 — не разведан, 1 — разведан, 2 — виден сейчас (`cells`). Читает чанками: на сверке и
  /// сохранении это вся карта.
  pub fn cells(&self, layers: &Layers, player: u32) -> Vec<u8> {
    let bounds = self.bounds;
    let width = bounds.width() as usize;
    if !self.fog {
      return vec![2; width * bounds.height() as usize];
    }
    let mut cells = vec![0u8; width * bounds.height() as usize];
    let owner = Owner::Player(player);
    let chunk = kharos_engine::layers::CHUNK;
    for cy in bounds.top.div_euclid(chunk)..=(bounds.bottom - 1).div_euclid(chunk) {
      for cx in bounds.left.div_euclid(chunk)..=(bounds.right - 1).div_euclid(chunk) {
        let explored = layers.chunk(self.explored, owner, cx, cy);
        let Some(explored) = explored else { continue };
        let sight = layers.chunk(self.sight, owner, cx, cy);
        for ty in 0..chunk {
          let y = cy * chunk + ty;
          if y < bounds.top || y >= bounds.bottom {
            continue;
          }
          for tx in 0..chunk {
            let x = cx * chunk + tx;
            if x < bounds.left || x >= bounds.right {
              continue;
            }
            let tile = (ty * chunk + tx) as usize;
            let at = (y - bounds.top) as usize * width + (x - bounds.left) as usize;
            cells[at] = if sight.is_some_and(|sight| sight[tile] != 0) {
              2
            } else if explored[tile] != 0 {
              1
            } else {
              0
            };
          }
        }
      }
    }
    cells
  }

  /// Ячейки стороной size, в которых игрок сейчас видит хоть один тайл, — ключами tileKey, в порядке открытия.
  pub fn seen_cells(&self, player: u32, size: i32) -> Vec<f64> {
    let bounds = self.bounds;
    let mut keys = Vec::new();
    if !self.fog {
      for y in bounds.top.div_euclid(size)..=(bounds.bottom - 1).div_euclid(size) {
        for x in bounds.left.div_euclid(size)..=(bounds.right - 1).div_euclid(size) {
          keys.push(tile_key(x, y));
        }
      }
      return keys;
    }
    let mut seen = HashSet::new();
    for &(x, y) in self.sights.get(&player).map_or(&[][..], |sight| &sight.lit) {
      let key = tile_key(x.div_euclid(size), y.div_euclid(size));
      if seen.insert(key.to_bits()) {
        keys.push(key);
      }
    }
    keys
  }

  /// Разведанное — длины чередующихся отрезков «не разведано», «разведано» по тайлам построчно (`map`).
  pub fn map(&self, layers: &Layers, player: u32) -> Vec<u32> {
    let mut runs = Vec::new();
    let (mut value, mut run) = (false, 0u32);
    for cell in self.cells(layers, player) {
      let next = cell != 0;
      if next != value {
        runs.push(run);
        value = next;
        run = 0;
      }
      run += 1;
    }
    runs.push(run);
    runs
  }

  /// Отмечает разведанным всё, что разведано в map (`explore`). Обзор игрока должен быть заведён (open).
  pub fn explore(&mut self, layers: &mut Layers, player: u32, map: &[u32]) {
    let bounds = self.bounds;
    let total = (bounds.width() * bounds.height()) as usize;
    let mut index = 0usize;
    for (i, &run) in map.iter().enumerate() {
      let end = total.min(index + run as usize);
      if i % 2 == 1 {
        for at in index..end {
          let (x, y) = (bounds.left + (at as i32 % bounds.width()), bounds.top + (at as i32 / bounds.width()));
          layers.set(self.explored, Owner::Player(player), x, y, 0, 1.0);
        }
      }
      index = end;
    }
    if let Some(sight) = self.sights.get_mut(&player) {
      sight.changed += 1;
    }
  }

  /// Забывает обзор и разведанное игрока: он начинает заново.
  pub fn forget(&mut self, layers: &mut Layers, player: u32) {
    if self.sights.remove(&player).is_some() {
      self.order.retain(|&other| other != player);
      layers.forget_player(player);
    }
  }

  /// Забывает всё.
  pub fn reset(&mut self, layers: &mut Layers) {
    for player in std::mem::take(&mut self.order) {
      self.sights.remove(&player);
      layers.forget_player(player);
    }
  }
}

/// С кем у игрока союз (`alliesIn`): номера из Player.allies.
fn allies_in(world: &World, player: u32) -> Vec<f64> {
  for entity in world.query(&["Player"]) {
    if number(world, entity, "Player", "id") == Some(player as f64) {
      return world.get(entity, "Player").and_then(|info| info.get("allies")).and_then(|allies| allies.as_array()).map(|allies| allies.iter().filter_map(|other| other.as_f64()).collect()).unwrap_or_default();
    }
  }
  Vec::new()
}
