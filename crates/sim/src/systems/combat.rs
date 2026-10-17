//! Бой (combat.ts, `fight`): снаряды летят и бьют, стрелки выбирают цели, гонятся и стреляют, взрывы, аварии и едкое
//! болото ранят, погибшие оставляют обломки, груз и остовы. Порядок дел и чисел — как в TS, до знака.

use crate::common::{alliances, building_hp, carrier_of, friend, gunners, hostile, is_own, is_wall, leave_home, on_turn, owner_of, protected_players, rect_distance, stance_of, weapon, weapon_of, NONE};
use crate::components::make;
use crate::data::{WeaponSpec, DATA};
use crate::inventory::Stock;
use crate::math::{js_atan2, js_cos, js_hypot, js_round, js_sin, turn_toward, wrap};
use crate::traces::{NewTrace, BURN, SCAR, WRECK};
use crate::values::{js_number, number, text};
use crate::vision::unit_sight;
use crate::walk::{flies, on_foot, order_move, Ground};
use crate::{Sim, Time};
use kharos_engine::terrain::SWAMP;
use kharos_engine::{Entity, World};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};

/// Раз во сколько тиков свободный стрелок высматривает цель.
const SCAN_TICKS: u64 = 5;
/// Насколько безобидная цель «дальше» опасной при выборе.
const HARMLESS: f64 = 1000.0;
/// Раз во сколько тиков едущий за целью сверяет путь к ней.
const CHASE_TICKS: u64 = 20;
const CHASE_MARGIN: f64 = 1.5;
/// На сколько тайлов дальше бьют турели зданий.
const TOWER_RANGE: f64 = 2.0;
/// Сколько поисков пути и тайлов за тик достаётся погоням.
const CHASE_SEARCHES: u32 = 16;
const CHASE_TILES: f64 = 15000.0;
const SHOOTER_STRIDE: u64 = 17;
const STUCK_LIMIT: f64 = 3.0;
/// Насколько стрелок может не довернуть к цели и всё же стрелять, радиан.
const AIM: f64 = 0.12;
const BEAM_TICKS: f64 = 3.0;
const CHAIN_RANGE: f64 = 2.5;
const CHAIN_DECAY: f64 = 0.6;
const OVERFLY: f64 = 1.5;
const SPLASH_EDGE: f64 = 0.5;
const MARK_CELL: f64 = 8.0;
const MARK_PAD: f64 = 2.0;
const SLOT_MARGIN: f64 = 0.5;
const SLOT_GAP: f64 = 0.4;
const SLOT_REACH: f64 = 0.75;
const SLOT_PATIENCE: f64 = 10.0;
const SLOT_REPLAN: f64 = 4.0;
const SLOT_CELL: f64 = 2.5;
const GUARD_RANGE: f64 = 4.0;
const FULL_RING: f64 = 0.25;
/// Дальше этого от места, где стоял, юнит в обороне за врагом не гонится (`LEASH` в tactics.ts).
const LEASH: f64 = 8.0;
/// Сколько секунд пробитый купол щита не держит (`SHIELD_DOWN`).
const SHIELD_DOWN: f64 = 10.0;
/// Взрыв меньше этого фундамент не трескает.
const SLAB_BLAST: f64 = 0.5;
/// Запас боеприпасов юнита на столько секунд огня (supply.ts).
const MAGAZINE_SECONDS: f64 = 50.0;
const INFANTRY_MAGAZINE_SECONDS: f64 = 120.0;
const EPSILON: f64 = 1e-9;
/// Красные пустоши — номер биома.
const RED_WASTES: u8 = 2;

/// Останавливает ли стена выстрел такого вида.
fn stops_at_wall(shot: &str) -> bool {
  !matches!(shot, "rocket" | "arc")
}

/// Всё, во что можно попасть: юнит или здание (`Mark`).
struct Mark {
  entity: Entity,
  player: f64,
  x: f64,
  y: f64,
  left: f64,
  top: f64,
  width: f64,
  height: f64,
  radius: f64,
  air: bool,
  armor: &'static str,
  wall: bool,
  hp: f64,
  value: f64,
  hit: f64,
  arms: Option<u8>,
}

fn distance_to(mark: &Mark, x: f64, y: f64) -> f64 {
  0f64.max(rect_distance(mark.left, mark.top, mark.width, mark.height, x, y) - mark.radius)
}

/// Метки по ячейкам (`gridOf`): каждая метка находится раз за поиск, в порядке ячеек.
struct Grid {
  cells: HashMap<(i64, i64), Vec<usize>>,
  seen: Vec<u32>,
  query: u32,
}

impl Grid {
  fn new(marks: &[Mark]) -> Self {
    let mut cells: HashMap<(i64, i64), Vec<usize>> = HashMap::new();
    for (index, mark) in marks.iter().enumerate() {
      let left = (mark.left / MARK_CELL).floor() as i64;
      let top = (mark.top / MARK_CELL).floor() as i64;
      let right = ((mark.left + mark.width) / MARK_CELL).floor() as i64;
      let bottom = ((mark.top + mark.height) / MARK_CELL).floor() as i64;
      for y in top..=bottom {
        for x in left..=right {
          cells.entry((x, y)).or_default().push(index);
        }
      }
    }
    Self { cells, seen: vec![0; marks.len()], query: 0 }
  }

  fn near(&mut self, x: f64, y: f64, radius: f64) -> Vec<usize> {
    self.query += 1;
    let mut found = Vec::new();
    let reach = radius + MARK_PAD;
    let bottom = ((y + reach) / MARK_CELL).floor() as i64;
    let right = ((x + reach) / MARK_CELL).floor() as i64;
    for cell_y in ((y - reach) / MARK_CELL).floor() as i64..=bottom {
      for cell_x in ((x - reach) / MARK_CELL).floor() as i64..=right {
        let Some(cell) = self.cells.get(&(cell_x, cell_y)) else { continue };
        for &index in cell {
          if self.seen[index] == self.query {
            continue;
          }
          self.seen[index] = self.query;
          found.push(index);
        }
      }
    }
    found
  }
}

/// Купол щита, который гасит урон в этот тик (`Dome`).
struct Dome {
  entity: Entity,
  player: f64,
  x: f64,
  y: f64,
  radius: f64,
  charge: f64,
  hit: f64,
  down: f64,
}

/// Состояние оружия (`Armed`).
#[derive(Clone, Copy)]
struct Armed {
  target: f64,
  chase: bool,
  cooldown: f64,
  stuck: f64,
  ordered: bool,
}

fn armed_of(world: &World, entity: Entity) -> Option<Armed> {
  let armed = world.get(entity, "Armed")?;
  let number = |name| armed.get(name).and_then(Value::as_f64).unwrap_or(0.0);
  let flag = |name| armed.get(name).and_then(Value::as_bool).unwrap_or(false);
  Some(Armed { target: number("target"), chase: flag("chase"), cooldown: number("cooldown"), stuck: number("stuck"), ordered: flag("ordered") })
}

fn put_armed(world: &mut World, entity: Entity, armed: Armed) {
  let fields = [("target", js_number(armed.target)), ("chase", json!(armed.chase)), ("cooldown", js_number(armed.cooldown)), ("stuck", js_number(armed.stuck)), ("ordered", json!(armed.ordered))];
  world.set(entity, "Armed", fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect());
}

/// Гонящийся за целью носитель (`Pursuer`).
struct Pursuer {
  entity: Entity,
  x: f64,
  y: f64,
  radius: f64,
  reach: f64,
  foot: bool,
  settled: bool,
  player: f64,
}

/// Взрыв тика: ground — в землю, такой оставляет гарь.
struct Blast {
  x: f64,
  y: f64,
  size: f64,
  ground: bool,
}

/// То, что поворачивается: юнит или турель (`turnerOf`).
struct Turner {
  entity: Entity,
  turret: bool,
  turn: f64,
  radius: f64,
}

impl Turner {
  fn of(world: &World, entity: Entity) -> Option<Self> {
    if let Some(kind) = text(world, entity, "Unit", "type") {
      let spec = &DATA.units[kind];
      return Some(Self { entity, turret: false, turn: spec.turn, radius: spec.radius });
    }
    let spec = &DATA.turrets[text(world, entity, "Turret", "type")?];
    Some(Self { entity, turret: true, turn: spec.turn, radius: spec.radius })
  }

  /// Поворот носителя турели: она хранит угол относительно него.
  fn base(&self, world: &World) -> f64 {
    let hull = world.get(self.entity, "Attached").and_then(|attached| attached.get("parent")).and_then(Value::as_f64);
    hull.and_then(|hull| number(world, hull as Entity, "Unit", "facing")).unwrap_or(0.0)
  }

  fn facing(&self, world: &World) -> f64 {
    if self.turret {
      wrap(self.base(world) + number(world, self.entity, "Turret", "angle").unwrap_or(0.0))
    } else {
      number(world, self.entity, "Unit", "facing").unwrap_or(0.0)
    }
  }

  fn set_facing(&self, world: &mut World, value: f64) {
    if self.turret {
      let angle = wrap(value - self.base(world));
      world.set(self.entity, "Turret", Map::from_iter([("angle".to_owned(), js_number(angle))]));
    } else {
      world.set(self.entity, "Unit", Map::from_iter([("facing".to_owned(), js_number(value))]));
    }
  }
}

/// Сколько стволов у юнита: своё оружие и вооружённые турели.
fn guns_of(kind: &str) -> f64 {
  let spec = &DATA.units[kind];
  let mut guns = if spec.weapon.is_some() { 1.0 } else { 0.0 };
  for mount in &spec.mounts {
    if DATA.turrets[&mount.turret].weapon.is_some() {
      guns += 1.0;
    }
  }
  guns
}

/// Полный магазин юнита (`magazineOf`).
fn magazine_of(kind: &str) -> f64 {
  DATA.units[kind].ammo.unwrap_or(0.0)
}

/// Сколько боеприпасов тратит выстрел юнита этим оружием (`shotCost`).
fn shot_cost(kind: &str, weapon: &WeaponSpec) -> f64 {
  let capacity = magazine_of(kind);
  let seconds = if DATA.units[kind].kind == "infantry" { INFANTRY_MAGAZINE_SECONDS } else { MAGAZINE_SECONDS };
  if capacity != 0.0 {
    (capacity * weapon.reload) / (seconds * guns_of(kind).max(1.0))
  } else {
    0.0
  }
}

/// Самый дешёвый выстрел юнита (`cheapestShot`).
fn cheapest_shot(kind: &str) -> f64 {
  let spec = &DATA.units[kind];
  let mut weapons: Vec<&str> = Vec::new();
  if let Some(own) = &spec.weapon {
    weapons.push(own);
  }
  for mount in &spec.mounts {
    if let Some(own) = &DATA.turrets[&mount.turret].weapon {
      weapons.push(own);
    }
  }
  weapons.iter().map(|own| shot_cost(kind, weapon(own))).fold(f64::INFINITY, f64::min).min(if weapons.is_empty() { 0.0 } else { f64::INFINITY })
}

/// Сколько боеприпасов при юните (`ammoOf`).
fn ammo_of(world: &World, entity: Entity) -> f64 {
  if let Some(ammo) = number(world, entity, "Magazine", "ammo") {
    return ammo;
  }
  text(world, entity, "Unit", "type").map(magazine_of).unwrap_or(0.0)
}

/// Бой: всё, что знает тик боя о мире, — метки, сетка, купола, союзы, погибшие и взрывы.
struct Fight {
  marks: Vec<Mark>,
  index: HashMap<Entity, usize>,
  grid: Grid,
  dead: Vec<Entity>,
  dead_set: HashSet<Entity>,
  blasts: Vec<Blast>,
  safe: HashSet<u64>,
  friends: HashMap<u64, Vec<f64>>,
  domes: Vec<Dome>,
  tick: u64,
  step: f64,
  unit_ammo: bool,
}

impl Fight {
  fn mark(&self, entity: Entity) -> Option<usize> {
    self.index.get(&entity).copied()
  }

  fn kill(&mut self, entity: Entity) {
    if self.dead_set.insert(entity) {
      self.dead.push(entity);
    }
  }

  fn can_hit(&self, weapon: &WeaponSpec, player: f64, mark: usize) -> bool {
    let mark = &self.marks[mark];
    hostile(player, mark.player) && !friend(&self.friends, player, mark.player) && !self.safe.contains(&(player as u64)) && !self.safe.contains(&(mark.player as u64)) && (if mark.air { weapon.air } else { !weapon.only_air }) && !self.dead_set.contains(&mark.entity)
  }

  /// Может ли метка навредить такому стрелку (`threatens`).
  fn threatens(&mut self, world: &World, mark: usize, air: bool) -> bool {
    let arms = match self.marks[mark].arms {
      Some(arms) => arms,
      None => {
        let mut arms = 0;
        for gunner in gunners(world, self.marks[mark].entity) {
          let Some(own) = weapon_of(world, gunner) else { continue };
          if !world.has(gunner, "Armed") {
            continue;
          }
          let spec = weapon(own);
          arms |= (if spec.only_air { 0 } else { 1 }) | (if spec.air { 2 } else { 0 });
        }
        self.marks[mark].arms = Some(arms);
        arms
      }
    };
    arms & (if air { 2 } else { 1 }) != 0
  }

  fn holds(&self, dome: usize) -> bool {
    let dome = &self.domes[dome];
    dome.charge > 0.0 && self.tick as f64 >= dome.down
  }

  /// Списывает урон с запаса купола (`drain`).
  fn drain(&mut self, dome: usize, damage: f64) -> f64 {
    let tick = self.tick as f64;
    let down = js_round(SHIELD_DOWN / self.step);
    let dome = &mut self.domes[dome];
    let taken = dome.charge.min(damage);
    dome.charge -= taken;
    dome.hit = tick;
    if dome.charge <= 0.0 {
      dome.charge = 0.0;
      dome.down = tick + down;
    }
    taken
  }

  /// Гасит урон куполом над целью (`absorb`); отдаёт, сколько прошло.
  #[allow(clippy::too_many_arguments)]
  fn absorb(&mut self, player: f64, x: f64, y: f64, damage: f64, from_x: f64, from_y: f64, from_air: bool) -> f64 {
    let mut best: Option<usize> = None;
    for index in 0..self.domes.len() {
      if !self.holds(index) {
        continue;
      }
      let dome = &self.domes[index];
      if js_hypot(x - dome.x, y - dome.y) > dome.radius {
        continue;
      }
      if !from_air && js_hypot(from_x - dome.x, from_y - dome.y) <= dome.radius {
        continue;
      }
      if dome.player != player && !friend(&self.friends, dome.player, player) {
        continue;
      }
      if best.is_none_or(|best| dome.charge > self.domes[best].charge) {
        best = Some(index);
      }
    }
    match best {
      None => damage,
      Some(best) => damage - self.drain(best, damage),
    }
  }

  /// Купол, о который разобьётся снаряд, и точка на его краю (`stopper`).
  #[allow(clippy::too_many_arguments)]
  fn stopper(&self, player: f64, x: f64, y: f64, to_x: f64, to_y: f64, from_air: bool) -> Option<(usize, f64, f64)> {
    let mut best: Option<(usize, f64, f64, f64)> = None;
    let (dx, dy) = (to_x - x, to_y - y);
    for index in 0..self.domes.len() {
      let dome = &self.domes[index];
      if !self.holds(index) || dome.player == player || friend(&self.friends, dome.player, player) {
        continue;
      }
      let inside = js_hypot(x - dome.x, y - dome.y) <= dome.radius;
      if inside {
        if from_air && best.is_none_or(|best| best.3 > 0.0) {
          best = Some((index, x, y, 0.0));
        }
        continue;
      }
      let (fx, fy) = (x - dome.x, y - dome.y);
      let a = dx * dx + dy * dy;
      if a == 0.0 {
        continue;
      }
      let b = 2.0 * (fx * dx + fy * dy);
      let c = fx * fx + fy * fy - dome.radius * dome.radius;
      let disc = b * b - 4.0 * a * c;
      if disc < 0.0 {
        continue;
      }
      let at = (-b - disc.sqrt()) / (2.0 * a);
      if !(0.0..=1.0).contains(&at) || best.is_some_and(|best| best.3 <= at) {
        continue;
      }
      best = Some((index, x + dx * at, y + dy * at, at));
    }
    best.map(|(dome, x, y, _)| (dome, x, y))
  }

  /// Наносит урон метке (`hit`): source — кто стрелял, (from_x, from_y) — откуда.
  #[allow(clippy::too_many_arguments)]
  fn hit(&mut self, world: &mut World, mark: usize, amount: f64, weapon: &WeaponSpec, source: Entity, from_x: f64, from_y: f64) {
    let entity = self.marks[mark].entity;
    if self.dead_set.contains(&entity) {
      return;
    }
    self.marks[mark].hit = self.tick as f64;
    let mut damage = amount * weapon.vs.get(self.marks[mark].armor).copied().unwrap_or(1.0);
    if !self.domes.is_empty() {
      let from_air = self.mark(source).is_some_and(|from| self.marks[from].air);
      let (player, x, y) = (self.marks[mark].player, self.marks[mark].x, self.marks[mark].y);
      damage = self.absorb(player, x, y, damage, from_x, from_y, from_air);
    }
    self.marks[mark].value -= damage / self.marks[mark].hp;
    if self.marks[mark].value <= 0.0 {
      self.kill(entity);
      return;
    }
    let Some(from) = self.mark(source) else { return };
    let patrols = world.get(entity, "Tactics").and_then(|tactics| tactics.get("patrol")).and_then(Value::as_array).is_some_and(|patrol| !patrol.is_empty());
    if world.has(entity, "Path") && !patrols {
      return;
    }
    let unit = world.has(entity, "Unit");
    let stance = if unit { stance_of(world, entity).to_owned() } else { "defensive".to_owned() };
    if stance == "passive" {
      return;
    }
    let (player, air) = (self.marks[mark].player, self.marks[mark].air);
    for gunner in gunners(world, entity) {
      let (Some(mut armed), Some(own)) = (armed_of(world, gunner), weapon_of(world, gunner)) else { continue };
      if !self.can_hit(crate::common::weapon(own), player, from) {
        continue;
      }
      if armed.target != NONE {
        let busy = self.mark(armed.target as Entity);
        if armed.ordered || busy.is_none_or(|busy| self.threatens(world, busy, air)) {
          continue;
        }
      }
      armed.target = source as f64;
      armed.chase = unit && stance != "hold";
      armed.ordered = false;
      armed.stuck = 0.0;
      put_armed(world, gunner, armed);
      if armed.chase {
        leave_home(world, entity);
      }
    }
  }

  /// Попадание в точку (`impact`): урон цели или всем врагам в радиусе взрыва; blocked — только стенам.
  #[allow(clippy::too_many_arguments)]
  fn impact(&mut self, world: &mut World, weapon: &WeaponSpec, player: f64, source: Entity, target: f64, x: f64, y: f64, from_x: f64, from_y: f64, blocked: bool) {
    let Some(splash) = weapon.splash else {
      if let Some(mark) = (target >= 0.0).then(|| self.mark(target as Entity)).flatten() {
        if self.can_hit(weapon, player, mark) {
          self.hit(world, mark, weapon.damage, weapon, source, from_x, from_y);
        }
      }
      return;
    };
    for mark in self.grid.near(x, y, splash) {
      if blocked && !self.marks[mark].wall {
        continue;
      }
      if !self.can_hit(weapon, player, mark) {
        continue;
      }
      let distance = distance_to(&self.marks[mark], x, y);
      if distance > splash {
        continue;
      }
      self.hit(world, mark, weapon.damage * (1.0 - (1.0 - SPLASH_EDGE) * (distance / splash)), weapon, source, from_x, from_y);
    }
  }
}

/// Метки мира: юниты, потом здания (`collectMarks`).
fn collect_marks(world: &World) -> (Vec<Mark>, HashMap<Entity, usize>) {
  let mut marks = Vec::new();
  let mut index = HashMap::new();
  let health = |entity| (number(world, entity, "Health", "value").unwrap_or(0.0), number(world, entity, "Health", "hit").unwrap_or(-1.0));
  for entity in world.query(&["Position", "Unit", "Owner", "Health"]) {
    let spec = &DATA.units[text(world, entity, "Unit", "type").unwrap_or_default()];
    let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    let (value, hit) = health(entity);
    let armor: &'static str = match spec.kind.as_str() {
      "infantry" => "infantry",
      "vehicle" => "vehicle",
      "heavy" => "heavy",
      _ => "air",
    };
    index.insert(entity, marks.len());
    marks.push(Mark { entity, player: owner_of(world, entity), x, y, left: x, top: y, width: 0.0, height: 0.0, radius: spec.radius, air: spec.kind == "air", armor, wall: false, hp: spec.hp, value, hit, arms: None });
  }
  for entity in world.query(&["Position", "Building", "Owner", "Health"]) {
    let kind = text(world, entity, "Building", "type").unwrap_or_default();
    let spec = &DATA.buildings[kind];
    let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    let (value, hit) = health(entity);
    let mark = Mark { entity, player: owner_of(world, entity), x: x + spec.width / 2.0, y: y + spec.height / 2.0, left: x, top: y, width: spec.width, height: spec.height, radius: 0.0, air: false, armor: "building", wall: is_wall(world, entity), hp: building_hp(kind), value, hit, arms: None };
    match index.get(&entity) {
      Some(&at) => marks[at] = mark,
      None => {
        index.insert(entity, marks.len());
        marks.push(mark);
      }
    }
  }
  (marks, index)
}

/// Купола, которые держатся сейчас (`domes`).
fn collect_domes(sim: &Sim, tick: u64) -> Vec<Dome> {
  let world = &sim.world;
  let mut result = Vec::new();
  for entity in world.query(&["Shield", "Building", "Owner"]) {
    let field = |name| number(world, entity, "Shield", name).unwrap_or(0.0);
    let (charge, hit, down) = (field("charge"), field("hit"), field("down"));
    if charge <= 0.0 || (tick as f64) < down || world.has(entity, "Site") {
      continue;
    }
    if sim.power.get(&entity).copied().unwrap_or(0.0) == 0.0 {
      continue;
    }
    let spec = &DATA.buildings[text(world, entity, "Building", "type").unwrap_or_default()];
    let Some(shield) = &spec.shield else { continue };
    let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    result.push(Dome { entity, player: owner_of(world, entity), x: x + spec.width / 2.0, y: y + spec.height / 2.0, radius: shield.radius, charge, hit, down });
  }
  result
}

/// Первая чужая стена на отрезке выстрела и точка входа в неё (`wallOnPath`).
#[allow(clippy::too_many_arguments)]
fn wall_on_path(sim: &Sim, player: f64, x0: f64, y0: f64, x1: f64, y1: f64, friends: &HashMap<u64, Vec<f64>>) -> Option<(Entity, f64, f64)> {
  let (dx, dy) = (x1 - x0, y1 - y0);
  if dx == 0.0 && dy == 0.0 {
    return None;
  }
  let (last_x, last_y) = (x1.floor() as i32, y1.floor() as i32);
  let step_x = if dx > 0.0 { 1 } else { -1 };
  let step_y = if dy > 0.0 { 1 } else { -1 };
  let delta_x = if dx != 0.0 { (1.0 / dx).abs() } else { f64::INFINITY };
  let delta_y = if dy != 0.0 { (1.0 / dy).abs() } else { f64::INFINITY };
  let (mut tile_x, mut tile_y) = (x0.floor() as i32, y0.floor() as i32);
  let mut cross_x = if dx != 0.0 { (if dx > 0.0 { tile_x as f64 + 1.0 - x0 } else { x0 - tile_x as f64 }) / dx.abs() } else { f64::INFINITY };
  let mut cross_y = if dy != 0.0 { (if dy > 0.0 { tile_y as f64 + 1.0 - y0 } else { y0 - tile_y as f64 }) / dy.abs() } else { f64::INFINITY };
  let mut t = 0.0;
  for _ in 0..64 {
    if let Some(building) = sim.occupancy.at(&sim.layers, tile_x, tile_y) {
      if is_wall(&sim.world, building) && !is_own(&sim.world, player, building) && !friend(friends, player, owner_of(&sim.world, building)) {
        return Some((building, x0 + dx * t, y0 + dy * t));
      }
    }
    if tile_x == last_x && tile_y == last_y {
      return None;
    }
    if cross_x < cross_y {
      t = cross_x;
      tile_x += step_x;
      cross_x += delta_x;
    } else {
      t = cross_y;
      tile_y += step_y;
      cross_y += delta_y;
    }
  }
  None
}

/// Места вокруг цели для гонящихся (`slotsAround`).
fn slots_around(sim: &mut Sim, fight: &mut Fight, target: usize, pursuers: &[Pursuer]) -> HashMap<Entity, (f64, f64)> {
  let mut slots = HashMap::new();
  let mut taken: HashMap<(i64, i64), Vec<(f64, f64, f64)>> = HashMap::new();
  let cell = |x: f64, y: f64| ((x / SLOT_CELL).floor() as i64, (y / SLOT_CELL).floor() as i64);
  let free = |taken: &HashMap<(i64, i64), Vec<(f64, f64, f64)>>, x: f64, y: f64, radius: f64| {
    let (cell_x, cell_y) = cell(x, y);
    for dy in -1..=1 {
      for dx in -1..=1 {
        for &(other_x, other_y, other) in taken.get(&(cell_x + dx, cell_y + dy)).map(Vec::as_slice).unwrap_or_default() {
          if js_hypot(other_x - x, other_y - y) < other + radius + SLOT_GAP {
            return false;
          }
        }
      }
    }
    true
  };
  let mark = &fight.marks[target];
  let (target_entity, target_radius, width, height) = (mark.entity, mark.radius, mark.width, mark.height);
  let center_x = mark.left + width / 2.0;
  let center_y = mark.top + height / 2.0;
  let mut order: Vec<&Pursuer> = pursuers.iter().collect();
  order.sort_by(|a, b| {
    let by_settled = (b.settled as i32 - a.settled as i32) as f64;
    let key = if by_settled != 0.0 { by_settled } else { js_hypot(a.x - center_x, a.y - center_y) - js_hypot(b.x - center_x, b.y - center_y) };
    key.partial_cmp(&0.0).unwrap_or(std::cmp::Ordering::Equal)
  });
  let mut full: Vec<(f64, f64)> = Vec::new();
  let bounds = sim.vision.bounds;
  for pursuer in order {
    if pursuer.settled {
      taken.entry(cell(pursuer.x, pursuer.y)).or_default().push((pursuer.x, pursuer.y, pursuer.radius));
      continue;
    }
    let bearing = js_atan2(pursuer.y - center_y, pursuer.x - center_x);
    let ring = pursuer.reach + target_radius + width.min(height) / 2.0;
    if full.iter().any(|&(other, radius)| (other - ring).abs() < FULL_RING && radius <= pursuer.radius) {
      continue;
    }
    let step = (pursuer.radius + SLOT_GAP / 2.0) / ring.max(1.0);
    let mut k = 0.0;
    while k * step <= std::f64::consts::PI {
      let angles = if k != 0.0 { vec![bearing + k * step, bearing - k * step] } else { vec![bearing] };
      let mut found = None;
      for angle in angles {
        let (dx, dy) = (js_cos(angle), js_sin(angle));
        let edge = (if dx != 0.0 { width / 2.0 / dx.abs() } else { f64::INFINITY }).min(if dy != 0.0 { height / 2.0 / dy.abs() } else { f64::INFINITY });
        let out = (if edge.is_finite() { edge } else { 0.0 }) + target_radius + pursuer.reach;
        let (x, y) = (center_x + dx * out, center_y + dy * out);
        let walkable = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).is_walkable(x.floor() as i32, y.floor() as i32, pursuer.foot);
        if !walkable || !free(&taken, x, y, pursuer.radius) {
          continue;
        }
        if k * step > std::f64::consts::PI / 2.0 && guarded(fight, target_entity, x, y, pursuer.player) {
          continue;
        }
        found = Some((x, y));
        break;
      }
      k += 1.0;
      let Some((x, y)) = found else { continue };
      slots.insert(pursuer.entity, (x, y));
      taken.entry(cell(x, y)).or_default().push((x, y, pursuer.radius));
      break;
    }
    if !slots.contains_key(&pursuer.entity) {
      full.push((ring, pursuer.radius));
    }
  }
  slots
}

/// Есть ли у места другие враги, кроме самой цели.
fn guarded(fight: &mut Fight, target: Entity, x: f64, y: f64, player: f64) -> bool {
  fight.grid.near(x, y, GUARD_RANGE).into_iter().any(|index| {
    let mark = &fight.marks[index];
    mark.entity != target && hostile(player, mark.player) && !friend(&fight.friends, player, mark.player) && !fight.dead_set.contains(&mark.entity) && distance_to(mark, x, y) <= GUARD_RANGE
  })
}

/// Новая сущность с компонентами по порядку, как world.spawn в TS.
fn spawn(world: &mut World, parts: Vec<(&str, Value)>) -> Entity {
  let entity = world.spawn();
  for (key, data) in parts {
    world.add(entity, key, data);
  }
  entity
}

fn position(x: f64, y: f64) -> Value {
  make("Position", [("x", js_number(x)), ("y", js_number(y))])
}

/// Взрыв в точке: сколько тиков он виден.
fn blast_life(size: f64, step: f64) -> f64 {
  js_round((0.25 + size * 0.2) / step)
}

fn add_trace(sim: &mut Sim, trace: NewTrace, tick: u64) {
  sim.traces.add(&mut sim.layers, &trace, tick);
}

fn trace(kind: u8, x: f64, y: f64) -> NewTrace {
  NewTrace { kind, x, y, entity: 0, unit: 0, facing: 0.0, player: 0, size: 0.0 }
}

/// Снимает грузовик с работы (`releaseHauler`).
fn release_hauler(world: &mut World, truck: Entity) {
  if !world.has(truck, "Hauler") {
    return;
  }
  let fields = [("mine", json!(-1)), ("pickup", json!(-1)), ("serve", json!([])), ("supply", json!(-1)), ("route", json!([])), ("stop", json!(0)), ("order", Value::Null), ("from", json!(-1)), ("to", json!(-1)), ("amount", json!(0)), ("full", json!(false)), ("loading", json!(false)), ("waiting", json!(false))];
  world.set(truck, "Hauler", fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect());
}

/// Реактор с топливом и шахта с готовой боеголовкой взрываются (`meltdown`).
pub(super) fn meltdown(world: &mut World, entity: Entity) {
  let Some(kind) = text(world, entity, "Building", "type") else { return };
  let spec = &DATA.buildings[kind];
  let Some(blast) = &spec.meltdown else { return };
  let Some((x, y)) = number(world, entity, "Position", "x").zip(number(world, entity, "Position", "y")) else { return };
  if let Some(burns) = &spec.burns {
    let stock = Stock::of(world, entity);
    let rods = stock.as_ref().map_or(0.0, |stock| stock.amount(&burns.good));
    if rods <= 0.0 && number(world, entity, "Reactor", "left").unwrap_or(0.0) <= 0.0 {
      return;
    }
    if let Some(mut stock) = stock.filter(|_| rods > 0.0) {
      stock.take(&burns.good, rods);
      stock.save(world, entity);
    }
  } else if world.get(entity, "Silo").and_then(|silo| silo.get("ready")).and_then(Value::as_bool) != Some(true) {
    return;
  }
  spawn(world, vec![("Position", position(x + spec.width / 2.0, y + spec.height / 2.0)), ("Burst", make("Burst", [("radius", js_number(blast.radius)), ("damage", js_number(blast.damage))]))]);
}

/// Бросает груз на тайл: к дропу, что уже там, или новым (`dropItems`).
pub fn drop_items(world: &mut World, x: f64, y: f64, items: &Map<String, Value>) {
  let amount = |good: &str| items.get(good).and_then(Value::as_f64).unwrap_or(0.0);
  let goods: Vec<&String> = DATA.goods.iter().filter(|good| amount(good) > EPSILON).collect();
  if goods.is_empty() {
    return;
  }
  let (tile_x, tile_y) = (x.floor(), y.floor());
  let existing = world.query(&["Position", "Drop"]).into_iter().find(|&drop| number(world, drop, "Position", "x") == Some(tile_x) && number(world, drop, "Position", "y") == Some(tile_y));
  let drop = existing.unwrap_or_else(|| spawn(world, vec![("Position", position(tile_x, tile_y)), ("Drop", make("Drop", [])), ("Inventory", make("Inventory", []))]));
  let mut stock = Stock::of(world, drop).expect("склад дропа");
  for good in goods {
    let value = stock.amount(good) + amount(good);
    stock.items.insert(good.clone(), js_number(value));
  }
  let capacity = stock.load();
  let items = stock.items;
  world.set(drop, "Inventory", Map::from_iter([("items".to_owned(), Value::Object(items)), ("capacity".to_owned(), js_number(capacity))]));
}

/// Бросает весь груз сущности на землю под ней (`dropCargo`).
fn drop_cargo(world: &mut World, entity: Entity) {
  let Some(stock) = Stock::of(world, entity) else { return };
  let Some((x, y)) = number(world, entity, "Position", "x").zip(number(world, entity, "Position", "y")) else { return };
  if world.has(entity, "Drop") {
    return;
  }
  let kind = text(world, entity, "Building", "type").or_else(|| if world.has(entity, "Unit") { None } else { text(world, entity, "Site", "type") });
  let (width, height) = kind.map_or((0.0, 0.0), |kind| (DATA.buildings[kind].width, DATA.buildings[kind].height));
  drop_items(world, x + (width / 2.0).floor(), y + (height / 2.0).floor(), &stock.items);
}

/// Наземный взрыв разбивает фундамент (`breakSlabs`).
fn break_slabs(sim: &mut Sim, x: f64, y: f64, size: f64) {
  if size < SLAB_BLAST {
    return;
  }
  let paving = sim.layers.id("paving").expect("нет слоя paving");
  let radius = size / 2.0;
  let mut broken = Vec::new();
  for tile_y in (y - radius).floor() as i32..=(y + radius).floor() as i32 {
    for tile_x in (x - radius).floor() as i32..=(x + radius).floor() as i32 {
      let hit = tile_x == x.floor() as i32 && tile_y == y.floor() as i32;
      if !hit && js_hypot(tile_x as f64 + 0.5 - x, tile_y as f64 + 0.5 - y) > radius {
        continue;
      }
      let ground = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, sim.vision.bounds, &sim.rules);
      if ground.is_paved(false, tile_x, tile_y) {
        sim.layers.set(paving, kharos_engine::Owner::World, tile_x, tile_y, 0, 0.0);
        sim.lifted.push((tile_x, tile_y));
      }
      // Работа над плитой — последняя из сущностей Pave на тайле, как paving.at в TS.
      let work = sim.world.query(&["Position", "Pave"]).into_iter().filter(|&pave| number(&sim.world, pave, "Position", "x") == Some(tile_x as f64) && number(&sim.world, pave, "Position", "y") == Some(tile_y as f64)).max();
      if let Some(work) = work.filter(|&work| text(&sim.world, work, "Pave", "kind") == Some("foundation")) {
        broken.push(work);
      }
    }
  }
  for entity in broken {
    sim.world.destroy(entity);
  }
}

/// Хватит ли носителю боеприпасов на выстрел (`hasAmmo`).
fn has_ammo(world: &World, unit_ammo: bool, carrier: Entity, weapon: &WeaponSpec) -> bool {
  if let Some(kind) = text(world, carrier, "Unit", "type") {
    if !unit_ammo {
      return true;
    }
    let cost = shot_cost(kind, weapon);
    return cost == 0.0 || ammo_of(world, carrier) >= cost - EPSILON;
  }
  let Some(kind) = text(world, carrier, "Building", "type") else { return true };
  let Some(ammo) = weapon.ammo.filter(|_| DATA.buildings[kind].ammo) else { return true };
  Stock::of(world, carrier).is_some_and(|stock| stock.amount("ammo") >= ammo - EPSILON)
}

/// Списывает боеприпасы на выстрел (`spend`); false — стрелять нечем.
fn spend(world: &mut World, unit_ammo: bool, carrier: Entity, weapon: &WeaponSpec) -> bool {
  if !has_ammo(world, unit_ammo, carrier, weapon) {
    return false;
  }
  if let Some(kind) = text(world, carrier, "Unit", "type") {
    let cost = shot_cost(kind, weapon);
    if !unit_ammo || cost == 0.0 {
      return true;
    }
    match number(world, carrier, "Magazine", "ammo") {
      Some(ammo) => world.set(carrier, "Magazine", Map::from_iter([("ammo".to_owned(), js_number(0f64.max(ammo - cost)))])),
      None => {
        let full = magazine_of(kind);
        world.add(carrier, "Magazine", make("Magazine", [("ammo", js_number(0f64.max(full - cost)))]));
      }
    }
    return true;
  }
  if let (Some(mut stock), Some(ammo)) = (Stock::of(world, carrier), weapon.ammo) {
    stock.take("ammo", ammo);
    stock.save(world, carrier);
  }
  true
}

/// Пуст ли магазин юнита (`isEmpty`).
fn is_empty(world: &World, unit_ammo: bool, entity: Entity) -> bool {
  let Some(kind) = text(world, entity, "Unit", "type") else { return false };
  if !unit_ammo || magazine_of(kind) == 0.0 {
    return false;
  }
  ammo_of(world, entity) < cheapest_shot(kind) - EPSILON
}

/// Летит ли снаряд, пока ещё не долетел.
struct Landed {
  entity: Entity,
  weapon: &'static WeaponSpec,
  player: f64,
  source: Entity,
  target: f64,
  x: f64,
  y: f64,
  from_x: f64,
  from_y: f64,
  blocked: bool,
  air: bool,
}

fn field(world: &World, entity: Entity, key: &str, name: &str) -> f64 {
  number(world, entity, key, name).unwrap_or(0.0)
}

fn flag(world: &World, entity: Entity, key: &str, name: &str) -> bool {
  world.get(entity, key).and_then(|data| data.get(name)).and_then(Value::as_bool).unwrap_or(false)
}

fn set(world: &mut World, entity: Entity, key: &str, fields: Vec<(&str, Value)>) {
  world.set(entity, key, fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect());
}

pub fn fight(sim: &mut Sim, time: Time) {
  let (marks, index) = collect_marks(&sim.world);
  let grid = Grid::new(&marks);
  let mut fight = Fight {
    marks,
    index,
    grid,
    dead: Vec::new(),
    dead_set: HashSet::new(),
    blasts: Vec::new(),
    safe: protected_players(&sim.world, time.tick),
    friends: alliances(&sim.world),
    domes: collect_domes(sim, time.tick),
    tick: time.tick,
    step: time.step,
    unit_ammo: sim.flag("unitAmmo"),
  };

  // Взрывы и следы лучей доживают свой срок.
  let mut gone = Vec::new();
  for entity in sim.world.query(&["Blast"]) {
    let age = field(&sim.world, entity, "Blast", "age") + 1.0;
    set(&mut sim.world, entity, "Blast", vec![("age", js_number(age))]);
    if age >= field(&sim.world, entity, "Blast", "life") {
      gone.push(entity);
    }
  }

  // Снаряды летят. Попадания — после обхода.
  let mut landed = Vec::new();
  for entity in sim.world.query(&["Shot", "Position"]) {
    let world = &mut sim.world;
    let shot_weapon = weapon(text(world, entity, "Shot", "weapon").unwrap_or_default());
    let age = field(world, entity, "Shot", "age") + 1.0;
    let life = field(world, entity, "Shot", "life");
    let Some(speed) = shot_weapon.speed else {
      set(world, entity, "Shot", vec![("age", js_number(age))]);
      if age >= life {
        gone.push(entity);
      }
      continue;
    };
    let (mut x, mut y) = (field(world, entity, "Position", "x"), field(world, entity, "Position", "y"));
    let (mut to_x, mut to_y) = (field(world, entity, "Shot", "toX"), field(world, entity, "Shot", "toY"));
    let target = field(world, entity, "Shot", "target");
    if shot_weapon.shot != "shell" {
      if let Some(mark) = (target >= 0.0).then(|| fight.mark(target as Entity)).flatten() {
        to_x = fight.marks[mark].x;
        to_y = fight.marks[mark].y;
      }
    }
    let shot = vec![("age", js_number(age)), ("prevX", js_number(x)), ("prevY", js_number(y)), ("toX", js_number(to_x)), ("toY", js_number(to_y))];
    let (dx, dy) = (to_x - x, to_y - y);
    let distance = js_hypot(dx, dy);
    let step = speed * time.step;
    let player = field(world, entity, "Shot", "player");
    if !fight.domes.is_empty() {
      let share = if distance > 0.0 { 1f64.min(step / distance) } else { 1.0 };
      let source = field(world, entity, "Shot", "source");
      let from_air = text(world, source as Entity, "Unit", "type").is_some_and(flies);
      if let Some((dome, stop_x, stop_y)) = fight.stopper(player, x, y, x + dx * share, y + dy * share, from_air) {
        fight.drain(dome, shot_weapon.damage);
        set(world, entity, "Shot", shot);
        set(world, entity, "Position", vec![("x", js_number(stop_x)), ("y", js_number(stop_y))]);
        fight.blasts.push(Blast { x: stop_x, y: stop_y, size: shot_weapon.splash.unwrap_or(0.2), ground: false });
        gone.push(entity);
        continue;
      }
    }
    if distance <= step || age >= life {
      let reached = distance <= step;
      if reached {
        (x, y) = (to_x, to_y);
      }
      set(world, entity, "Shot", shot);
      set(world, entity, "Position", vec![("x", js_number(x)), ("y", js_number(y))]);
      if reached || shot_weapon.splash.is_some() {
        let source = field(world, entity, "Shot", "source") as Entity;
        let (from_x, from_y) = (field(world, entity, "Shot", "fromX"), field(world, entity, "Shot", "fromY"));
        landed.push(Landed { entity, weapon: shot_weapon, player, source, target, x, y, from_x, from_y, blocked: flag(world, entity, "Shot", "blocked"), air: flag(world, entity, "Shot", "air") });
      } else {
        gone.push(entity);
      }
      continue;
    }
    x += (dx / distance) * step;
    y += (dy / distance) * step;
    set(world, entity, "Shot", shot);
    set(world, entity, "Position", vec![("x", js_number(x)), ("y", js_number(y))]);
  }
  for shot in landed {
    fight.impact(&mut sim.world, shot.weapon, shot.player, shot.source, shot.target, shot.x, shot.y, shot.from_x, shot.from_y, shot.blocked);
    fight.blasts.push(Blast { x: shot.x, y: shot.y, size: shot.weapon.splash.unwrap_or(0.2), ground: !shot.air });
    gone.push(shot.entity);
  }
  for entity in gone {
    sim.world.destroy(entity);
  }

  shoot(sim, &mut fight, time);
  finish(sim, &mut fight, time);
}

/// Стрелки: выбор цели, погоня, поворот и выстрел.
fn shoot(sim: &mut Sim, fight: &mut Fight, time: Time) {
  let shooters = sim.world.query(&["Armed"]);

  // Кто за какой целью гонится — наземные носители, на начало тика.
  let mut pursuers: Vec<(Entity, Vec<Pursuer>)> = Vec::new();
  for &entity in &shooters {
    let world = &sim.world;
    let Some(armed) = armed_of(world, entity) else { continue };
    if !armed.chase || armed.target == NONE {
      continue;
    }
    let carrier = carrier_of(world, entity);
    let (Some(kind), Some(own)) = (text(world, carrier, "Unit", "type"), weapon_of(world, entity)) else { continue };
    let Some((x, y)) = number(world, carrier, "Position", "x").zip(number(world, carrier, "Position", "y")) else { continue };
    if flies(kind) {
      continue;
    }
    let spec = weapon(own);
    let reach = (spec.min_range.unwrap_or(0.0) + SLOT_MARGIN).max(spec.range - SLOT_MARGIN);
    let target = fight.mark(armed.target as Entity);
    let at = match pursuers.iter().position(|(other, _)| *other == armed.target as Entity) {
      Some(at) => at,
      None => {
        pursuers.push((armed.target as Entity, Vec::new()));
        pursuers.len() - 1
      }
    };
    let around = &mut pursuers[at].1;
    let known = around.iter().position(|pursuer| pursuer.entity == carrier);
    if known.is_some_and(|known| around[known].reach <= reach) {
      continue;
    }
    let settled = !world.has(carrier, "Path") && target.is_some_and(|target| distance_to(&fight.marks[target], x, y) <= spec.range);
    let pursuer = Pursuer { entity: carrier, x, y, radius: DATA.units[kind].radius, reach, foot: on_foot(kind), settled, player: owner_of(world, carrier) };
    match known {
      Some(known) => around[known] = pursuer,
      None => around.push(pursuer),
    }
  }
  let mut slots: HashMap<Entity, HashMap<Entity, (f64, f64)>> = HashMap::new();

  let in_chase = |world: &World, carrier: Entity| gunners(world, carrier).into_iter().any(|gunner| armed_of(world, gunner).is_some_and(|armed| armed.chase && armed.target != NONE));

  let mut searches = 0;
  let mut chasing: HashSet<Entity> = HashSet::new();
  let searched_before = sim.search.searched();
  let first = if shooters.is_empty() { 0 } else { ((time.tick * SHOOTER_STRIDE) % shooters.len() as u64) as usize };
  for turn in 0..shooters.len() {
    let entity = shooters[(first + turn) % shooters.len()];
    let Some(mut armed) = armed_of(&sim.world, entity) else { continue };
    if armed.cooldown > 0.0 {
      armed.cooldown -= 1.0;
    }
    'shooter: {
      let world = &sim.world;
      let carrier = carrier_of(world, entity);
      let mounted = carrier != entity;
      let body = fight.mark(carrier);
      let place = number(world, entity, "Position", "x").zip(number(world, entity, "Position", "y"));
      let turner = Turner::of(world, entity);
      let weapon_type = weapon_of(world, entity);
      let (Some(body), Some((self_x, self_y)), Some(turner), Some(weapon_type)) = (body, place, turner, weapon_type) else { break 'shooter };
      if world.has(carrier, "Converting") || world.has(carrier, "Site") || world.has(carrier, "Off") {
        break 'shooter;
      }
      let self_player = fight.marks[body].player;
      let body_air = fight.marks[body].air;
      let spec = weapon(weapon_type);
      let range = spec.range + if world.has(carrier, "Building") { TOWER_RANGE } else { 0.0 };

      let mut target = if armed.target >= 0.0 { fight.mark(armed.target as Entity) } else { None };
      if target.is_some_and(|target| !fight.can_hit(spec, self_player, target)) {
        target = None;
      }
      let mover = carrier;
      let moving = world.has(mover, "Path");
      let unit_carrier = text(world, carrier, "Unit", "type").map(str::to_owned);
      if unit_carrier.is_some() && is_empty(world, fight.unit_ammo, carrier) {
        if armed.chase {
          sim.world.remove(mover, "Path");
        }
        armed.target = NONE;
        armed.chase = false;
        armed.ordered = false;
        break 'shooter;
      }
      let resupplying = flag(world, carrier, "Tactics", "resupply");
      let stance = if unit_carrier.is_some() { if resupplying { "hold".to_owned() } else { stance_of(world, carrier).to_owned() } } else { "defensive".to_owned() };
      let had_tactics = world.has(carrier, "Tactics");
      let patrol = world.get(carrier, "Tactics").and_then(|tactics| tactics.get("patrol")).and_then(Value::as_array).is_some_and(|patrol| !patrol.is_empty());
      let away = flag(world, carrier, "Tactics", "away");
      let patrolling = patrol || (stance == "aggressive" && away);
      let led = moving && !patrolling && !in_chase(world, carrier);
      let lookout = match &unit_carrier {
        Some(kind) if stance == "aggressive" && !led => range.max(unit_sight(kind)),
        _ => range,
      };
      let look = |fight: &mut Fight, world: &World, dangerous: bool| {
        let mut found = None;
        let mut best = f64::INFINITY;
        for mark in fight.grid.near(self_x, self_y, lookout) {
          if !fight.can_hit(spec, self_player, mark) {
            continue;
          }
          let danger = fight.threatens(world, mark, body_air);
          if dangerous && !danger {
            continue;
          }
          let distance = distance_to(&fight.marks[mark], self_x, self_y);
          if distance > lookout || distance < spec.min_range.unwrap_or(0.0) {
            continue;
          }
          let order = distance + (if fight.marks[mark].armor == "building" { range } else { 0.0 }) + (if danger { 0.0 } else { HARMLESS });
          if order >= best {
            continue;
          }
          best = order;
          found = Some(mark);
        }
        found
      };
      // Занятый целью, которую выбрал сам и которая ему не опасна, бросает её ради того, кто опасен.
      if let Some(current) = target {
        if !armed.ordered && stance != "passive" && on_turn(time.tick, entity, SCAN_TICKS) && !fight.threatens(world, current, body_air) {
          if let Some(danger) = look(fight, world, true) {
            target = Some(danger);
            armed.target = fight.marks[danger].entity as f64;
            armed.stuck = 0.0;
            if distance_to(&fight.marks[danger], self_x, self_y) > range && unit_carrier.is_some() {
              armed.chase = true;
              leave_home(&mut sim.world, carrier);
            }
          }
        }
      }
      let world = &sim.world;
      let target = match target {
        Some(target) => target,
        None => {
          armed.target = NONE;
          armed.chase = false;
          armed.ordered = false;
          if stance == "passive" || (moving && !mounted && !patrolling) || !on_turn(time.tick, entity, SCAN_TICKS) {
            break 'shooter;
          }
          let Some(found) = look(fight, world, moving && !patrol && away) else { break 'shooter };
          armed.target = fight.marks[found].entity as f64;
          if distance_to(&fight.marks[found], self_x, self_y) > range {
            armed.chase = true;
            leave_home(&mut sim.world, carrier);
          }
          found
        }
      };

      // Юнит в обороне, убежавший за врагом дальше поводка от места, где стоял, бросает погоню и вернётся туда.
      let world = &sim.world;
      if !armed.ordered && armed.chase && stance == "defensive" && had_tactics && flag(world, carrier, "Tactics", "away") {
        let (home_x, home_y) = (field(world, carrier, "Tactics", "homeX"), field(world, carrier, "Tactics", "homeY"));
        if js_hypot(self_x - home_x - 0.5, self_y - home_y - 0.5) > LEASH {
          armed.target = NONE;
          armed.chase = false;
          break 'shooter;
        }
      }

      let distance = distance_to(&fight.marks[target], self_x, self_y);
      if distance > range {
        if !armed.chase || !world.has(mover, "Unit") {
          armed.target = NONE;
          break 'shooter;
        }
        if mounted && chasing.contains(&mover) {
          break 'shooter;
        }
        let every = if moving { CHASE_TICKS } else { SCAN_TICKS << (armed.stuck.min(STUCK_LIMIT) as u32) };
        if !on_turn(time.tick, entity, every) {
          break 'shooter;
        }
        let target_entity = fight.marks[target].entity;
        if !slots.contains_key(&target_entity) {
          let around = pursuers.iter().find(|(other, _)| *other == target_entity).map(|(_, list)| list.as_slice()).unwrap_or_default();
          let made = slots_around(sim, fight, target, around);
          slots.insert(target_entity, made);
        }
        let world = &sim.world;
        let slot = slots[&target_entity].get(&mover).copied();
        let (goal_x, goal_y) = match slot {
          Some((x, y)) => (x.floor(), y.floor()),
          None => (fight.marks[target].x.floor(), fight.marks[target].y.floor()),
        };
        let (at_x, at_y) = (field(world, mover, "Position", "x"), field(world, mover, "Position", "y"));
        let close = slot.is_some_and(|(x, y)| js_hypot(at_x - x, at_y - y) < SLOT_REPLAN);
        if world.has(mover, "Path") && !close && flag(world, mover, "Path", "slot") == slot.is_some() && (field(world, mover, "Path", "goalX") - goal_x).abs() <= 1.0 && (field(world, mover, "Path", "goalY") - goal_y).abs() <= 1.0 {
          break 'shooter;
        }
        let over = searches >= CHASE_SEARCHES || sim.search.searched() - searched_before >= CHASE_TILES;
        searches += 1;
        if over {
          break 'shooter;
        }
        let near = if slot.is_some() { 0.0 } else { 0f64.max(spec.range - CHASE_MARGIN) + fight.marks[target].width.max(fight.marks[target].height) / 2.0 };
        chasing.insert(mover);
        let previous = sim.search.open_quota(CHASE_TILES - (sim.search.searched() - searched_before));
        order_move(sim, mover, goal_x as i32, goal_y as i32, None, 0.0, near, None);
        if sim.world.has(mover, "Path") {
          if slot.is_some() {
            set(&mut sim.world, mover, "Path", vec![("slot", json!(true))]);
          }
          armed.stuck = 0.0;
        } else if !sim.search.cut() {
          armed.stuck += 1.0;
        }
        sim.search.close_quota(previous);
        break 'shooter;
      }
      // В мёртвой зоне оружие молчит.
      if distance < spec.min_range.unwrap_or(0.0) {
        if !armed.chase {
          armed.target = NONE;
        }
        break 'shooter;
      }
      let path = moving.then(|| ());
      let (at_x, at_y) = (field(world, mover, "Position", "x"), field(world, mover, "Position", "y"));
      let to_slot = path.is_some() && flag(world, mover, "Path", "slot") && armed.chase && field(world, mover, "Path", "wait") < SLOT_PATIENCE && js_hypot(at_x - field(world, mover, "Path", "goalX") - 0.5, at_y - field(world, mover, "Path", "goalY") - 0.5) > SLOT_REACH;
      if to_slot && !mounted {
        break 'shooter;
      }
      if moving && !to_slot && (!mounted || armed.chase || patrolling) {
        sim.world.remove(mover, "Path");
      }

      let (target_x, target_y, target_air, target_entity) = (fight.marks[target].x, fight.marks[target].y, fight.marks[target].air, fight.marks[target].entity);
      let wanted = js_atan2(target_y - self_y, target_x - self_x);
      let facing = turner.facing(&sim.world);
      turner.set_facing(&mut sim.world, turn_toward(facing, wanted, turner.turn * time.step));
      if armed.cooldown > 0.0 || wrap(wanted - turner.facing(&sim.world)).abs() > AIM {
        break 'shooter;
      }
      let world = &sim.world;
      let power = match text(world, carrier, "Building", "type") {
        Some(kind) if DATA.buildings[kind].power.unwrap_or(0.0) < 0.0 => sim.power.get(&carrier).copied().unwrap_or(0.0),
        _ => 1.0,
      };
      if power == 0.0 || !spend(&mut sim.world, fight.unit_ammo, carrier, spec) {
        break 'shooter;
      }
      armed.cooldown = 1f64.max(js_round(spec.reload / time.step / power));

      // Выстрел — из точки перед стрелком.
      let facing = turner.facing(&sim.world);
      let from_x = self_x + js_cos(facing) * turner.radius;
      let from_y = self_y + js_sin(facing) * turner.radius;
      let base = |fields: Vec<(&'static str, Value)>| {
        let mut all = vec![("weapon", json!(weapon_type)), ("player", js_number(self_player)), ("source", json!(carrier)), ("fromX", js_number(from_x)), ("fromY", js_number(from_y)), ("prevX", js_number(from_x)), ("prevY", js_number(from_y))];
        all.extend(fields);
        make("Shot", all)
      };
      let over_wall = sim.world.has(carrier, "Building") || body_air || target_air || !stops_at_wall(&spec.shot) || spec.lob;
      let wall = if over_wall { None } else { wall_on_path(sim, self_player, self_x, self_y, target_x, target_y, &fight.friends) };
      let (hit_x, hit_y) = wall.map_or((target_x, target_y), |(_, x, y)| (x, y));
      if let Some(speed) = spec.speed {
        let reach = if spec.shot == "shell" { js_hypot(hit_x - from_x, hit_y - from_y) } else { range * OVERFLY };
        let life = 1f64.max((reach / speed / time.step).ceil());
        let target = wall.map_or(target_entity, |(wall, _, _)| wall);
        let shot = base(vec![("target", json!(target)), ("toX", js_number(hit_x)), ("toY", js_number(hit_y)), ("blocked", json!(wall.is_some())), ("air", json!(wall.is_none() && target_air)), ("life", js_number(life))]);
        spawn(&mut sim.world, vec![("Position", position(from_x, from_y)), ("Shot", shot)]);
        break 'shooter;
      }
      if let Some((wall, _, _)) = wall {
        let shot = base(vec![("target", json!(wall)), ("toX", js_number(hit_x)), ("toY", js_number(hit_y)), ("life", js_number(BEAM_TICKS))]);
        spawn(&mut sim.world, vec![("Position", position(from_x, from_y)), ("Shot", shot)]);
        add_trace(sim, trace(BURN, hit_x, hit_y), time.tick);
        if let Some(mark) = fight.mark(wall) {
          fight.hit(&mut sim.world, mark, spec.damage, spec, carrier, from_x, from_y);
        }
        break 'shooter;
      }
      // Лазер, разряд и огонь бьют сразу; разряд и огонь перескакивают дальше на ближайших врагов.
      let mut damage = spec.damage;
      let mut from = (from_x, from_y);
      let mut next = Some(target);
      let mut struck = HashSet::new();
      let mut jump = 0.0;
      while let Some(current) = next {
        if jump > spec.chain.unwrap_or(0.0) {
          break;
        }
        let (current_entity, current_x, current_y, current_air) = (fight.marks[current].entity, fight.marks[current].x, fight.marks[current].y, fight.marks[current].air);
        struck.insert(current_entity);
        let shot = base(vec![("fromX", js_number(from.0)), ("fromY", js_number(from.1)), ("target", json!(current_entity)), ("toX", js_number(current_x)), ("toY", js_number(current_y)), ("life", js_number(BEAM_TICKS))]);
        spawn(&mut sim.world, vec![("Position", position(from.0, from.1)), ("Shot", shot)]);
        fight.hit(&mut sim.world, current, damage, spec, carrier, from_x, from_y);
        if !current_air {
          add_trace(sim, trace(BURN, current_x, current_y), time.tick);
        }
        damage *= CHAIN_DECAY;
        from = (current_x, current_y);
        next = None;
        let mut nearest = CHAIN_RANGE;
        for mark in fight.grid.near(current_x, current_y, CHAIN_RANGE) {
          if struck.contains(&fight.marks[mark].entity) || !fight.can_hit(spec, self_player, mark) {
            continue;
          }
          let distance = distance_to(&fight.marks[mark], current_x, current_y);
          if distance >= nearest {
            continue;
          }
          nearest = distance;
          next = Some(mark);
        }
        jump += 1.0;
      }
    }
    put_armed(&mut sim.world, entity, armed);
  }
}

/// Сдавшиеся, аварии и удары, едкое болото, гибель и взрывы.
fn finish(sim: &mut Sim, fight: &mut Fight, time: Time) {
  let mut vanished = Vec::new();
  for entity in sim.world.query(&["Doomed"]) {
    let left = field(&sim.world, entity, "Doomed", "left") - 1.0;
    set(&mut sim.world, entity, "Doomed", vec![("left", js_number(left))]);
    if left > 0.0 {
      continue;
    }
    if fight.mark(entity).is_some() {
      fight.kill(entity);
    } else {
      vanished.push(entity);
    }
  }
  for entity in vanished {
    sim.world.destroy(entity);
  }

  // Аварии и ядерные удары бьют всех вокруг без разбора; под защитой новичка урона нет.
  let mut bursts = Vec::new();
  for entity in sim.world.query(&["Burst", "Position"]) {
    let world = &sim.world;
    bursts.push(entity);
    let (x, y) = (field(world, entity, "Position", "x"), field(world, entity, "Position", "y"));
    let (radius, damage, core) = (field(world, entity, "Burst", "radius"), field(world, entity, "Burst", "damage"), field(world, entity, "Burst", "core"));
    for index in fight.grid.near(x, y, radius) {
      let mark = &fight.marks[index];
      if fight.dead_set.contains(&mark.entity) || fight.safe.contains(&(mark.player as u64)) {
        continue;
      }
      let distance = distance_to(mark, x, y);
      if distance > radius {
        continue;
      }
      let share = if distance <= core { 1.0 } else { (radius - distance) / (radius - core) };
      let mark = &mut fight.marks[index];
      mark.hit = time.tick as f64;
      mark.value -= (damage * share) / mark.hp;
      if mark.value <= 0.0 {
        let entity = mark.entity;
        fight.kill(entity);
      }
    }
    fight.blasts.push(Blast { x, y, size: radius, ground: true });
  }
  for entity in bursts {
    sim.world.destroy(entity);
  }

  // Болото красных пустошей жжёт стоящих в нём наземных юнитов.
  let burn = sim.rule("toxicSwamp") * time.step;
  if burn > 0.0 {
    let bounds = sim.vision.bounds;
    for index in 0..fight.marks.len() {
      let mark = &fight.marks[index];
      if mark.air || mark.radius == 0.0 || fight.dead_set.contains(&mark.entity) {
        continue;
      }
      let (x, y) = (mark.x.floor() as i32, mark.y.floor() as i32);
      let [terrain, biome, ..] = sim.land.tile(x, y);
      if terrain != SWAMP || biome >> 6 != RED_WASTES {
        continue;
      }
      if Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).is_paved(true, x, y) {
        continue;
      }
      let mark = &mut fight.marks[index];
      mark.hit = time.tick as f64;
      mark.value -= burn;
      if mark.value <= 0.0 {
        let entity = mark.entity;
        fight.kill(entity);
      }
    }
  }

  // Прочность и купола — обратно в мир.
  for mark in &fight.marks {
    if fight.dead_set.contains(&mark.entity) {
      continue;
    }
    set(&mut sim.world, mark.entity, "Health", vec![("value", js_number(mark.value)), ("hit", js_number(mark.hit))]);
  }
  for dome in &fight.domes {
    set(&mut sim.world, dome.entity, "Shield", vec![("charge", js_number(dome.charge)), ("hit", js_number(dome.hit)), ("down", js_number(dome.down))]);
  }

  for entity in fight.dead.clone() {
    let mark = &fight.marks[fight.index[&entity]];
    let size = if mark.radius != 0.0 { mark.radius * 2.0 } else { mark.width.max(mark.height) * 0.7 };
    let (x, y, air) = (mark.x, mark.y, mark.air);
    fight.blasts.push(Blast { x, y, size, ground: !air });
    // От техники остаётся остов, от пехоты — только гарь.
    if let Some(kind) = text(&sim.world, entity, "Unit", "type").map(str::to_owned) {
      if !air && DATA.units[&kind].kind != "infantry" {
        let facing = field(&sim.world, entity, "Unit", "facing");
        add_trace(sim, NewTrace { unit: crate::traces::unit_index(&kind), facing, ..trace(WRECK, x, y) }, time.tick);
      }
    }
    release_hauler(&mut sim.world, entity);
    meltdown(&mut sim.world, entity);
    drop_cargo(&mut sim.world, entity);
    sim.world.destroy(entity);
  }
  for blast in std::mem::take(&mut fight.blasts) {
    let fields = vec![("size", js_number(blast.size)), ("life", js_number(blast_life(blast.size, time.step)))];
    spawn(&mut sim.world, vec![("Position", position(blast.x, blast.y)), ("Blast", make("Blast", fields))]);
    if blast.ground {
      add_trace(sim, NewTrace { size: blast.size, ..trace(SCAR, blast.x, blast.y) }, time.tick);
      break_slabs(sim, blast.x, blast.y, blast.size);
    }
  }
}
