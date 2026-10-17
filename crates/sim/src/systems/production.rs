//! Производство юнитов (production.ts, `produceUnits`): очередь заказов продвигается на долю энергии, заказ тратит
//! материалы в начале, готовый юнит выходит рядом с производителем и едет к точке сбора.

use crate::common::{owner_of, reward};
use crate::components;
use crate::data::DATA;
use crate::inventory::Stock;
use crate::math::js_round;
use crate::placement::send_to_tiles;
use crate::spawning::spawn_unit;
use crate::values::{js_number, number, text};
use crate::walk::{flies, radius_of, standing_units, Ground};
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};

/// Среди скольких ближайших тайлов ищется свободный выход для готового юнита.
const EXIT_CANDIDATES: usize = 24;

/// Свёрнутая ли база: машина с очередью главного здания (`isPacked`).
fn is_packed(world: &World, entity: Entity) -> bool {
  world.has(entity, "Unit") && world.has(entity, "Producer")
}

/// Сколько товара космопорта уже доставлено под заявку на продажу (`deliveredTo`).
fn delivered_to(world: &World, port: Entity) -> f64 {
  let Some(order) = world.get(port, "Trade") else { return 0.0 };
  if order.get("buy").and_then(Value::as_bool).unwrap_or(false) {
    return 0.0;
  }
  let resource = order.get("resource").and_then(Value::as_str).unwrap_or_default();
  let wanted = order.get("wanted").and_then(Value::as_f64).unwrap_or(0.0);
  Stock::of(world, port).map_or(0.0, |stock| wanted.min(stock.amount(resource)))
}

/// Сколько груза на складе можно потратить на нужды самого здания: всё, кроме товара на продажу (`spareOf`).
pub fn spare_of(world: &World, entity: Entity, good: &str) -> f64 {
  let Some(stock) = Stock::of(world, entity) else { return 0.0 };
  let reserved = if text(world, entity, "Trade", "resource") == Some(good) { delivered_to(world, entity) } else { 0.0 };
  0f64.max(stock.amount(good) - reserved)
}

/// Груз и сколько его, без нулей, в порядке записи (`entriesOf`).
fn entries(amounts: Option<&Map<String, Value>>) -> Vec<(String, f64)> {
  amounts.map_or_else(Vec::new, |amounts| amounts.iter().filter_map(|(good, amount)| amount.as_f64().filter(|&amount| amount > 0.0).map(|amount| (good.clone(), amount))).collect())
}

/// Материалы, которых ждёт сущность: стройка — на здание, производитель — на первый заказ, пока тот не начат.
pub fn materials_for(world: &World, entity: Entity) -> Option<&'static Map<String, Value>> {
  if let Some(site) = world.get(entity, "Site") {
    if site.get("demolish").and_then(Value::as_bool).unwrap_or(false) {
      return None;
    }
    return DATA.buildings[site.get("type").and_then(Value::as_str).unwrap_or_default()].materials.as_ref();
  }
  let producer = world.get(entity, "Producer")?;
  let head = producer.get("queue").and_then(Value::as_array).and_then(|queue| queue.first()).and_then(Value::as_str)?;
  if producer.get("progress").and_then(Value::as_f64).unwrap_or(0.0) > 0.0 {
    return None;
  }
  DATA.units[head].materials.as_ref()
}

/// Чего из материалов ещё нет на складе: пусто — всё на месте (`missingFor`).
pub fn missing_for(world: &World, entity: Entity) -> Vec<(String, f64)> {
  let mut missing = Vec::new();
  for (good, amount) in entries(materials_for(world, entity)) {
    let lack = amount - spare_of(world, entity, &good);
    // Крошку меньше погрешности не ждут.
    if lack > 1e-6 {
      missing.push((good, lack));
    }
  }
  missing
}

/// Тайл, вокруг которого появляются произведённые юниты (`exitOf`).
fn exit_of(world: &World, entity: Entity) -> (i32, i32) {
  let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
  match text(world, entity, "Building", "type") {
    Some(kind) => {
      let spec = &DATA.buildings[kind];
      ((x + (spec.width / 2.0).floor()) as i32, (y + (spec.height / 2.0).floor()) as i32)
    }
    None => (x.floor() as i32, y.floor() as i32),
  }
}

/// Ближайший к точке проходимый тайл без другого юнита; сама точка не в счёт (`emptyTileNear`).
fn empty_tile_near(sim: &mut Sim, x: i32, y: i32) -> Option<(i32, i32)> {
  let world = &sim.world;
  let taken: HashSet<(i32, i32)> = world.query(&["Position", "Unit"]).into_iter().map(|unit| (number(world, unit, "Position", "x").unwrap_or(0.0).floor() as i32, number(world, unit, "Position", "y").unwrap_or(0.0).floor() as i32)).collect();
  let bounds = sim.vision.bounds;
  let tiles = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).free_tiles_near(x, y, EXIT_CANDIDATES, 1, None, false);
  tiles.chunks_exact(2).map(|tile| (tile[0], tile[1])).find(|tile| !taken.contains(tile))
}

/// Раздаёт наземным тайлы и пускает их напрямую, без поиска пути (`aimAtTiles`).
fn aim_at_tiles(world: &mut World, units: &[Entity], tiles: &[i32]) {
  if tiles.is_empty() {
    return;
  }
  for (i, &entity) in units.iter().enumerate() {
    if world.has(entity, "Converting") {
      continue;
    }
    let at = (i * 2).min(tiles.len() - 2);
    let (x, y) = (tiles[at], tiles[at + 1]);
    let fields = [("points", json!([js_number(x as f64 + 0.5), js_number(y as f64 + 0.5)])), ("goalX", json!(x)), ("goalY", json!(y)), ("wait", json!(0)), ("tries", json!(0)), ("near", json!(0)), ("roads", json!(false)), ("direct", json!(true)), ("stuck", json!(false)), ("best", json!(-1)), ("slot", json!(false))];
    components::add(world, entity, "Path", fields);
  }
}

/// Посылает группу в точку: каждому своё место рядом; летающие и наземные — порознь (`orderGroupMove`).
pub fn order_group_move(sim: &mut Sim, all: &[Entity], x: i32, y: i32) {
  let kind = |world: &World, entity: Entity| text(world, entity, "Unit", "type").unwrap_or_default().to_owned();
  let air = flies(&kind(&sim.world, all[0]));
  let (units, rest): (Vec<Entity>, Vec<Entity>) = all.iter().partition(|&&entity| flies(&kind(&sim.world, entity)) == air);
  if !rest.is_empty() {
    order_group_move(sim, &rest, x, y);
  }
  let group: HashSet<Entity> = units.iter().copied().collect();
  let radius = units.iter().map(|&entity| radius_of(&kind(&sim.world, entity))).fold(f64::NEG_INFINITY, f64::max);
  let taken = standing_units(&sim.world, &group, radius, air, false);
  let bounds = sim.vision.bounds;
  let tiles = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).free_tiles_near(x, y, units.len(), 0, Some(&taken), air);
  if air {
    send_to_tiles(sim, &units, &tiles, &group);
  } else {
    aim_at_tiles(&mut sim.world, &units, &tiles);
  }
}

fn queue_of(world: &World, entity: Entity) -> Vec<Value> {
  world.get(entity, "Producer").and_then(|producer| producer.get("queue")).and_then(Value::as_array).cloned().unwrap_or_default()
}

pub fn produce_units(sim: &mut Sim, time: Time) {
  let world = &sim.world;
  // Скорость производства: потребителю энергии при её нехватке — доля, вне зоны — ноль.
  let mut speeds = HashMap::new();
  for entity in world.query(&["Producer"]) {
    if queue_of(world, entity).is_empty() {
      continue;
    }
    let speed = match text(world, entity, "Building", "type") {
      Some(kind) if DATA.buildings[kind].power.unwrap_or(0.0) < 0.0 => sim.power.get(&entity).copied().unwrap_or(0.0),
      _ => 1.0,
    };
    speeds.insert(entity, speed);
  }
  let mut ready = Vec::new();
  for entity in sim.world.query(&["Producer"]) {
    let world = &mut sim.world;
    let queue = queue_of(world, entity);
    if queue.is_empty() || world.has(entity, "Converting") || world.has(entity, "Site") || is_packed(world, entity) {
      continue;
    }
    let head = queue[0].as_str().unwrap_or_default();
    let needed = js_round(DATA.units[head].build_time / time.step);
    let mut progress = number(world, entity, "Producer", "progress").unwrap_or(0.0);
    if progress <= 0.0 {
      // Заказ начинается, когда его материалы на месте, и сразу их тратит.
      if !missing_for(world, entity).is_empty() {
        continue;
      }
      let materials = entries(DATA.units[head].materials.as_ref());
      if let Some(mut stock) = Stock::of(world, entity) {
        for (good, amount) in &materials {
          stock.take(good, *amount);
        }
        stock.save(world, entity);
      }
      if !materials.is_empty() {
        progress = 1e-9;
      }
    }
    if progress < needed {
      progress += speeds.get(&entity).copied().unwrap_or(1.0);
    }
    world.set(entity, "Producer", Map::from_iter([("progress".to_owned(), js_number(progress))]));
    if progress >= needed {
      ready.push(entity);
    }
  }

  // Юниты создаются после обхода.
  for entity in ready {
    let (x, y) = exit_of(&sim.world, entity);
    // Выйти некуда — готовый юнит ждёт внутри, очередь стоит.
    let Some((tile_x, tile_y)) = empty_tile_near(sim, x, y) else { continue };
    let player = owner_of(&sim.world, entity);
    let mut queue = queue_of(&sim.world, entity);
    let kind = queue.remove(0);
    sim.world.set(entity, "Producer", Map::from_iter([("queue".to_owned(), Value::Array(queue))]));
    let unit = spawn_unit(&mut sim.world, kind.as_str().unwrap_or_default(), player, tile_x as f64, tile_y as f64);
    let rally: Vec<f64> = sim.world.get(entity, "Producer").and_then(|producer| producer.get("rally")).and_then(Value::as_array).map(|rally| rally.iter().filter_map(Value::as_f64).collect()).unwrap_or_default();
    if !rally.is_empty() {
      order_group_move(sim, &[unit], rally[0] as i32, rally[1] as i32);
    }
    reward(&mut sim.world, player, "unit");
    sim.world.set(entity, "Producer", Map::from_iter([("progress".to_owned(), js_number(0.0))]));
  }
}
