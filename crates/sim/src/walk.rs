//! Где юнит может стоять и как быстро идёт по местности; приказ движения — прокладка пути (units.ts). Как в TS, до
//! последнего тайла: пути, проложенные здесь и в TS, одинаковы.

use crate::data::DATA;
use crate::occupancy::Occupancy;
use crate::path::{beyond_window, smooth_path, Grid, Search, SEARCH_LIMIT};
use crate::values::{js_number, number, text};
use crate::vision::{unit_sight, Bounds};
use crate::Sim;
use kharos_engine::terrain::{Land, FOOT_BIT, MOUNTAIN, SAND, SWAMP};
use kharos_engine::{Entity, LayerId, Layers, Owner, World};
use serde_json::{json, Map, Value};
use std::collections::HashSet;

/// Во сколько раз быстрее, чем по голой скале, идут по дороге и по фундаменту.
pub const ROAD_SPEED: f64 = 1.5;
pub const FOUNDATION_SPEED: f64 = 1.2;
/// Медленнее этой доли скорости местность не замедляет.
const SLOWEST: f64 = 0.05;
/// После скольких перепрокладок подряд юнит считается застрявшим в заторе.
pub const JAM_TRIES: f64 = 3.0;
/// Сколько тайлов осматривает поиск, когда к цели надо только подойти на расстояние.
const APPROACH_LIMIT: f64 = 3000.0;
/// Дальше скольких колец от цели места группе не ищутся.
const MAX_GROUP_RADIUS: i32 = 40;
/// Шаг между местами юнитов в группе, в тайлах.
const GROUP_SPACING: i32 = 2;
/// Во сколько раз дольше ищется путь, если обычный поиск не дошёл до цели.
const LONG_SEARCH: f64 = 4.0;
/// Сколько тайлов заливает проверка кармана перед долгим поиском.
const LONG_POCKET: usize = 1000;
/// На сколько колец от цели, в которую не встать, ищется тайл, куда встать можно.
const STAND_RADIUS: i32 = 8;
/// Номер солончаков в биоме тайла.
const SALT_FLATS: u8 = 1;

type Tiles = HashSet<(i32, i32)>;

/// Вид покрытия по коду (`paveKindOf`): PAVE_KINDS = foundation, road.
fn pave_road(code: u32) -> bool {
  (code - 2) % 2 == 1
}

/// Класс юнита этого вида.
pub fn kind_of(unit: &str) -> &'static str {
  DATA.units.get(unit).map_or("", |spec| spec.kind.as_str())
}

pub fn flies(unit: &str) -> bool {
  kind_of(unit) == "air"
}

pub fn on_foot(unit: &str) -> bool {
  kind_of(unit) == "infantry"
}

pub fn radius_of(unit: &str) -> f64 {
  DATA.units.get(unit).map_or(0.0, |spec| spec.radius)
}

/// Самая высокая доля скорости, какая бывает у юнита (`fastestOf`).
pub fn fastest_of(unit: &str) -> f64 {
  match kind_of(unit) {
    "heavy" | "air" => 1.0,
    _ => ROAD_SPEED,
  }
}

/// Земля под ногами: местность, покрытие, здания и правила мира — то, что решает, можно ли стоять и как быстро идти.
pub struct Ground<'a> {
  pub land: &'a mut Land,
  pub layers: &'a Layers,
  pub world: &'a World,
  pub occupancy: &'a Occupancy,
  pub bounds: Bounds,
  pub rules: &'a Value,
  paving: LayerId,
}

impl<'a> Ground<'a> {
  pub fn new(land: &'a mut Land, layers: &'a Layers, world: &'a World, occupancy: &'a Occupancy, bounds: Bounds, rules: &'a Value) -> Self {
    Self { land, layers, world, occupancy, bounds, rules, paving: layers.id("paving").expect("нет слоя paving") }
  }

  fn rule(&self, name: &str) -> f64 {
    self.rules.get(name).and_then(Value::as_f64).unwrap_or(0.0)
  }

  pub fn in_bounds(&self, x: i32, y: i32) -> bool {
    x >= self.bounds.left && y >= self.bounds.top && x < self.bounds.right && y < self.bounds.bottom
  }

  /// Может ли наземный юнит находиться в тайле (`isWalkable`): проходимая местность, не подножие обрыва (кроме
  /// пехоты), не здание — кроме открытых ворот.
  pub fn is_walkable(&mut self, x: i32, y: i32, foot: bool) -> bool {
    if !self.in_bounds(x, y) {
      return false;
    }
    let [terrain, _, _, relief] = self.land.tile(x, y);
    if terrain == MOUNTAIN || (!foot && relief & FOOT_BIT != 0) {
      return false;
    }
    match self.occupancy.at(self.layers, x, y) {
      None => true,
      Some(building) => self.world.get(building, "Gate").and_then(|gate| gate.get("open")).and_then(Value::as_bool) == Some(true) && !self.world.has(building, "Site"),
    }
  }

  /// Может ли юнит находиться в тайле (`canStand`): летающему — любой внутри карты.
  pub fn can_stand(&mut self, air: bool, x: i32, y: i32, foot: bool) -> bool {
    if air {
      self.in_bounds(x, y)
    } else {
      self.is_walkable(x, y, foot)
    }
  }

  fn pave(&self, x: i32, y: i32) -> u32 {
    self.layers.get(self.paving, Owner::World, x, y, 0) as u32
  }

  /// Лежит ли на тайле готовая дорога (road) или фундамент (`isPaved`).
  pub fn is_paved(&self, road: bool, x: i32, y: i32) -> bool {
    let code = self.pave(x, y);
    code != 0 && pave_road(code) == road
  }

  /// Хватает ли юниту ширины покрытия в тайле (`pavedFor`): юниту шире тайла — полоса в два.
  fn paved_for(&self, unit: &str, x: i32, y: i32) -> bool {
    let width = if radius_of(unit) > 0.5 { 2 } else { 1 };
    for top in y - width + 1..=y {
      'search: for left in x - width + 1..=x {
        for tile_y in top..top + width {
          for tile_x in left..left + width {
            if self.pave(tile_x, tile_y) == 0 {
              continue 'search;
            }
          }
        }
        return true;
      }
    }
    false
  }

  /// Доля полной скорости юнита на тайле (`terrainSpeed`).
  pub fn terrain_speed(&mut self, unit: &str, x: i32, y: i32) -> f64 {
    let kind = kind_of(unit);
    if kind == "air" {
      return 1.0;
    }
    let road = self.is_paved(true, x, y);
    if road || self.is_paved(false, x, y) {
      if kind == "heavy" || !self.paved_for(unit, x, y) {
        return 1.0;
      }
      return if road { ROAD_SPEED } else { FOUNDATION_SPEED };
    }
    let [terrain, biome, _, relief] = self.land.tile(x, y);
    if kind == "infantry" && terrain != MOUNTAIN && relief & FOOT_BIT != 0 {
      return SLOWEST.max(1.0 - self.rule("infantryCliff"));
    }
    if terrain == SAND {
      return SLOWEST.max(1.0 - self.rule(&format!("{kind}Sand")));
    }
    if terrain == SWAMP {
      let frozen = if biome >> 6 == SALT_FLATS { self.rule("frozenSwamp") } else { 1.0 };
      return SLOWEST.max(1.0 - self.rule(&format!("{kind}Swamp")) * frozen);
    }
    1.0
  }

  /// Видит ли юнит готовую дорогу в радиусе обзора (`roadInSight`).
  pub fn road_in_sight(&self, entity: Entity) -> bool {
    if self.layers.cells(self.paving, Owner::World).is_empty() {
      return false;
    }
    let (Some(x), Some(y), Some(unit)) = (number(self.world, entity, "Position", "x"), number(self.world, entity, "Position", "y"), text(self.world, entity, "Unit", "type")) else { return false };
    let sight = unit_sight(unit);
    let (center_x, center_y) = (x.floor() as i32, y.floor() as i32);
    let reach = sight.ceil() as i32;
    for dy in -reach..=reach {
      for dx in -reach..=reach {
        if ((dx * dx + dy * dy) as f64) > sight * sight {
          continue;
        }
        if self.is_paved(true, center_x + dx, center_y + dy) {
          return true;
        }
      }
    }
    false
  }

  /// Куда техника доедет из тайла заливкой (`vehicleReach`).
  pub fn vehicle_reach(&mut self, x: i32, y: i32, limit: usize, foot: bool) -> Tiles {
    let mut reached = Tiles::from([(x, y)]);
    let mut queue = vec![(x, y)];
    let mut at = 0;
    while at < queue.len() && reached.len() < limit {
      let (from_x, from_y) = queue[at];
      for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
        let tile = (from_x + dx, from_y + dy);
        if reached.contains(&tile) || !self.is_walkable(tile.0, tile.1, foot) {
          continue;
        }
        reached.insert(tile);
        queue.push(tile);
      }
      at += 1;
    }
    reached
  }

  /// Проходимые тайлы вокруг точки, от ближних к дальним, через GROUP_SPACING (`freeTilesNear`).
  #[allow(clippy::too_many_arguments)]
  pub fn free_tiles_near(&mut self, x: i32, y: i32, count: usize, from_radius: i32, blocked: Option<&Tiles>, air: bool) -> Vec<i32> {
    let search_radius = MAX_GROUP_RADIUS.min(8.max((count as f64).sqrt().ceil() as i32));
    let mut tiles = Vec::new();
    let mut radius = from_radius;
    while radius <= search_radius && tiles.len() < count * 2 {
      for dy in -radius..=radius {
        for dx in -radius..=radius {
          if dx.abs().max(dy.abs()) != radius {
            continue;
          }
          let (tile_x, tile_y) = (x + dx * GROUP_SPACING, y + dy * GROUP_SPACING);
          if tiles.len() >= count * 2 || blocked.is_some_and(|blocked| blocked.contains(&(tile_x, tile_y))) {
            continue;
          }
          if self.can_stand(air, tile_x, tile_y, false) {
            tiles.push(tile_x);
            tiles.push(tile_y);
          }
        }
      }
      radius += 1;
    }
    tiles
  }

  /// Ближайший к (x, y) тайл, где наземный может стоять и нет стоящих (`nearestStand`).
  #[allow(clippy::too_many_arguments)]
  fn nearest_stand(&mut self, x: i32, y: i32, from_x: i32, from_y: i32, foot: bool, taken: &Tiles) -> Option<(i32, i32)> {
    for ring in 1..=STAND_RADIUS {
      let mut best = None;
      let mut best_order = f64::INFINITY;
      for dy in -ring..=ring {
        for dx in -ring..=ring {
          if dx.abs().max(dy.abs()) != ring {
            continue;
          }
          let (tile_x, tile_y) = (x + dx, y + dy);
          if !self.is_walkable(tile_x, tile_y, foot) || taken.contains(&(tile_x, tile_y)) {
            continue;
          }
          let order = ((dx * dx + dy * dy) as f64) * 1e6 + ((tile_x - from_x) as f64).powi(2) + ((tile_y - from_y) as f64).powi(2);
          if order >= best_order {
            continue;
          }
          best = Some((tile_x, tile_y));
          best_order = order;
        }
      }
      if best.is_some() {
        return best;
      }
    }
    None
  }
}

/// Тайлы, занятые стоящими юнитами (`standingUnits`): путь их обходит.
pub fn standing_units(world: &World, ignore: &HashSet<Entity>, radius: f64, air: bool, jammed: bool) -> Tiles {
  let mut tiles = Tiles::new();
  for entity in world.query(&["Position", "Unit"]) {
    let unit = text(world, entity, "Unit", "type").unwrap_or_default();
    if ignore.contains(&entity) || flies(unit) != air {
      continue;
    }
    if world.has(entity, "Path") && !(jammed && number(world, entity, "Path", "tries").unwrap_or(0.0) >= JAM_TRIES) {
      continue;
    }
    let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    let reach = radius_of(unit) + radius;
    let (right, bottom) = ((x + reach).floor() as i32, (y + reach).floor() as i32);
    for tile_y in (y - reach).floor() as i32..=bottom {
      let dy = tile_y as f64 + 0.5 - y;
      for tile_x in (x - reach).floor() as i32..=right {
        let dx = tile_x as f64 + 0.5 - x;
        if dx * dx + dy * dy < reach * reach {
          tiles.insert((tile_x, tile_y));
        }
      }
    }
  }
  tiles
}

/// Сетка поиска пути юнита: свой тайл проходим всегда, занятое стоящими — нет; цена шага — по местности.
struct Walker<'g, 'a> {
  ground: &'g mut Ground<'a>,
  from: (i32, i32),
  foot: bool,
  unit: &'g str,
  taken: &'g Tiles,
}

impl Grid for Walker<'_, '_> {
  fn walkable(&mut self, x: i32, y: i32) -> bool {
    (x, y) == self.from || (self.ground.is_walkable(x, y, self.foot) && !self.taken.contains(&(x, y)))
  }

  fn slowness(&mut self, x: i32, y: i32) -> f64 {
    1.0 / self.ground.terrain_speed(self.unit, x, y)
  }
}

/// Чем кончился приказ движения.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Moved {
  /// Проложен новый путь.
  Set,
  /// Идти некуда: пути больше нет.
  Removed,
  /// Путь остался прежним.
  Kept,
}

/// Кладёт юниту путь: новый компонент — со всеми полями по умолчанию, как Path() в TS; есть — поля поверх.
fn put_path(world: &mut World, entity: Entity, fields: Vec<(&str, Value)>) {
  let patch: Map<String, Value> = fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect();
  if world.has(entity, "Path") {
    world.set(entity, "Path", patch);
    return;
  }
  let mut data = json!({ "points": [], "goalX": 0, "goalY": 0, "wait": 0, "tries": 0, "near": 0, "roads": false, "direct": false, "stuck": false, "best": -1, "slot": false });
  if let Value::Object(fields) = &mut data {
    for (name, value) in patch {
      fields.insert(name, value);
    }
  }
  world.add(entity, "Path", data);
}

/// Отправляет юнит в тайл (x, y): прокладывает путь и кладёт его в Path (`orderMove`).
#[allow(clippy::too_many_arguments)]
pub fn order_move(sim: &mut Sim, entity: Entity, mut x: i32, mut y: i32, ignore: Option<&HashSet<Entity>>, tries: f64, near: f64, limit: Option<f64>) -> Moved {
  let world = &sim.world;
  let (Some(px), Some(py)) = (number(world, entity, "Position", "x"), number(world, entity, "Position", "y")) else { return Moved::Kept };
  if world.has(entity, "Converting") {
    return Moved::Kept;
  }
  let unit = text(world, entity, "Unit", "type").unwrap_or_default().to_owned();
  let air = flies(&unit);
  let jam = tries >= JAM_TRIES;
  let own = HashSet::from([entity]);
  let taken = standing_units(world, ignore.unwrap_or(&own), radius_of(&unit), air, jam);
  let mut ground = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, sim.vision.bounds, &sim.rules);
  if near == 0.0 && taken.contains(&(x, y)) {
    let free = ground.free_tiles_near(x, y, 1, 1, Some(&taken), air);
    if free.is_empty() {
      sim.world.remove(entity, "Path");
      return Moved::Removed;
    }
    (x, y) = (free[0], free[1]);
  }
  if air {
    if !ground.in_bounds(x, y) {
      sim.world.remove(entity, "Path");
      return Moved::Removed;
    }
    let fields = vec![("points", json!([js_number(x as f64 + 0.5), js_number(y as f64 + 0.5)])), ("goalX", json!(x)), ("goalY", json!(y)), ("wait", json!(0)), ("tries", js_number(tries)), ("near", js_number(near)), ("direct", json!(false)), ("stuck", json!(false)), ("best", json!(-1)), ("slot", json!(false))];
    put_path(&mut sim.world, entity, fields);
    return Moved::Set;
  }
  let (from_x, from_y) = (px.floor() as i32, py.floor() as i32);
  let foot = on_foot(&unit);
  if near == 0.0 && (x, y) != (from_x, from_y) && !ground.is_walkable(x, y, foot) {
    if let Some(stand) = ground.nearest_stand(x, y, from_x, from_y, foot, &taken) {
      (x, y) = stand;
    }
  }
  let fastest = if fastest_of(&unit) > 1.0 && ground.road_in_sight(entity) { 1.0 / fastest_of(&unit) } else { 1.0 };
  let search: &mut Search = &mut sim.search;
  let mut walker = Walker { ground: &mut ground, from: (from_x, from_y), foot, unit: &unit, taken: &taken };
  let mut tiles = search.find_path(&mut walker, from_x, from_y, x, y, near, if near != 0.0 { APPROACH_LIMIT } else { limit.unwrap_or(SEARCH_LIMIT) }, fastest);
  let reaches = |path: &[i32]| {
    if path.is_empty() {
      return from_x == x && from_y == y;
    }
    let (dx, dy) = ((path[path.len() - 2] - x) as f64, (path[path.len() - 1] - y) as f64);
    dx * dx + dy * dy <= near * near
  };
  // Поиск оборвала норма тика, а не преграда: прежний путь остаётся как был.
  if search.cut() && !reaches(&tiles) {
    return Moved::Kept;
  }
  if near == 0.0 && !reaches(&tiles) && !beyond_window(from_x, from_y, x, y) {
    let pocket = walker.ground.vehicle_reach(x, y, LONG_POCKET, foot);
    if pocket.len() >= LONG_POCKET || pocket.contains(&(from_x, from_y)) {
      let longer = search.find_path(&mut walker, from_x, from_y, x, y, near, limit.unwrap_or(SEARCH_LIMIT) * LONG_SEARCH, fastest);
      if reaches(&longer) {
        tiles = longer;
      }
    }
  }
  if near != 0.0 && tiles.is_empty() && (((from_x - x) * (from_x - x) + (from_y - y) * (from_y - y)) as f64) <= near * near {
    return order_move(sim, entity, x, y, ignore, tries, 0.0, Some(APPROACH_LIMIT));
  }
  let centers: Vec<f64> = tiles.iter().map(|&value| value as f64 + 0.5).collect();
  let points = smooth_path(&mut walker, px, py, &centers);
  if !points.is_empty() {
    let fields = vec![("points", Value::Array(points.into_iter().map(js_number).collect())), ("goalX", json!(x)), ("goalY", json!(y)), ("wait", json!(0)), ("tries", js_number(tries)), ("near", js_number(near)), ("roads", json!(fastest < 1.0)), ("direct", json!(false)), ("stuck", json!(false)), ("best", json!(-1)), ("slot", json!(false))];
    put_path(&mut sim.world, entity, fields);
    Moved::Set
  } else if !jam {
    sim.world.remove(entity, "Path");
    Moved::Removed
  } else {
    Moved::Kept
  }
}
