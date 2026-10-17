//! Новые юниты и здания (`spawnUnit` в units.ts, `placeBuilding` в buildings.ts): компоненты — в том же порядке и с
//! теми же полями, что в TS, вместе с турелями и складами.

use crate::components::make;
use crate::data::{Mount, Storage, DATA};
use crate::math::{js_cos, js_sin};
use crate::values::{js_number, number, text};
use kharos_engine::{Entity, World};
use serde_json::{json, Value};

/// Какую долю прочности в секунду пехота восстанавливает сама (`INFANTRY_REGEN`).
const INFANTRY_REGEN: f64 = 0.02;
/// Склад здания, которое производит юнитов, если своего у вида нет (`PRODUCER_HOLD`).
const PRODUCER_HOLD: f64 = 100.0;

/// Новая сущность с компонентами по порядку, как world.spawn в TS.
pub fn spawn(world: &mut World, parts: Vec<(&str, Value)>) -> Entity {
  let entity = world.spawn();
  for (key, data) in parts {
    world.add(entity, key, data);
  }
  entity
}

fn add(world: &mut World, entity: Entity, key: &str, fields: Vec<(&'static str, Value)>) {
  world.add(entity, key, make(key, fields));
}

/// Склад и луч по виду (`equipStorage`).
fn equip_storage(world: &mut World, entity: Entity, spec: &Storage) {
  if let Some(capacity) = spec.inventory.filter(|&capacity| capacity != 0.0) {
    let accepts = Value::Array(spec.accepts.iter().map(|good| json!(good)).collect());
    add(world, entity, "Inventory", vec![("capacity", js_number(capacity)), ("accepts", accepts), ("limits", Value::Object(spec.limits.clone()))]);
  }
  if let Some(beam) = &spec.beam {
    add(world, entity, "Beam", vec![("radius", js_number(beam.radius)), ("rate", js_number(beam.rate)), ("give", json!(beam.give)), ("take", json!(beam.take))]);
  }
}

/// Турели на носителе по его виду (`mountTurrets`).
fn mount_turrets(world: &mut World, entity: Entity) {
  let mounts: &[Mount] = if let Some(kind) = text(world, entity, "Unit", "type") {
    &DATA.units[kind].mounts
  } else if let Some(kind) = text(world, entity, "Building", "type") {
    &DATA.buildings[kind].mounts
  } else {
    return;
  };
  let Some((x, y)) = number(world, entity, "Position", "x").zip(number(world, entity, "Position", "y")) else { return };
  let (cx, cy, facing) = match text(world, entity, "Unit", "type") {
    Some(_) => (x, y, number(world, entity, "Unit", "facing").unwrap_or(0.0)),
    None => {
      let spec = &DATA.buildings[text(world, entity, "Building", "type").unwrap_or_default()];
      (x + spec.width / 2.0, y + spec.height / 2.0, 0.0)
    }
  };
  let player = number(world, entity, "Owner", "player").unwrap_or(0.0);
  let mut turrets = Vec::new();
  for mount in mounts {
    let at_x = cx + mount.along * js_cos(facing) - mount.across * js_sin(facing);
    let at_y = cy + mount.along * js_sin(facing) + mount.across * js_cos(facing);
    let spec = &DATA.turrets[&mount.turret];
    let turret = spawn(
      world,
      vec![
        ("Position", make("Position", [("x", js_number(at_x)), ("y", js_number(at_y))])),
        ("Owner", make("Owner", [("player", js_number(player))])),
        ("Attached", make("Attached", [("parent", json!(entity)), ("along", js_number(mount.along)), ("across", js_number(mount.across))])),
        ("Turret", make("Turret", [("type", json!(mount.turret)), ("prevX", js_number(at_x)), ("prevY", js_number(at_y))])),
      ],
    );
    if spec.weapon.is_some() {
      add(world, turret, "Armed", vec![]);
    }
    if let Some(radius) = spec.repair.filter(|&radius| radius != 0.0) {
      add(world, turret, "Repair", vec![("radius", js_number(radius))]);
    }
    turrets.push(json!(turret));
  }
  add(world, entity, "Carrier", vec![("turrets", Value::Array(turrets))]);
}

/// Новый юнит в тайле (x, y) — в его середине (`spawnUnit`).
pub fn spawn_unit(world: &mut World, kind: &str, player: f64, x: f64, y: f64) -> Entity {
  let (x, y) = (x + 0.5, y + 0.5);
  let spec = &DATA.units[kind];
  let health = if spec.kind == "infantry" { make("Health", [("repairable", json!(false)), ("regen", js_number(INFANTRY_REGEN))]) } else { make("Health", []) };
  let entity = spawn(
    world,
    vec![
      ("Position", make("Position", [("x", js_number(x)), ("y", js_number(y))])),
      ("Unit", make("Unit", [("type", json!(kind)), ("prevX", js_number(x)), ("prevY", js_number(y))])),
      ("Owner", make("Owner", [("player", js_number(player))])),
      ("Health", health),
    ],
  );
  if kind == "mcv" {
    add(world, entity, "Producer", vec![]);
  }
  if spec.hauls {
    add(world, entity, "Hauler", vec![]);
  }
  if spec.weapon.is_some() {
    add(world, entity, "Armed", vec![]);
  }
  if let Some(ammo) = spec.ammo.filter(|&ammo| ammo != 0.0) {
    add(world, entity, "Magazine", vec![("ammo", js_number(ammo))]);
  }
  if let Some(radius) = spec.repair.filter(|&radius| radius != 0.0) {
    add(world, entity, "Repair", vec![("radius", js_number(radius))]);
  }
  equip_storage(world, entity, &spec.storage);
  // Carrier — только у вида с турелями: пустых списков в данных нет.
  if !spec.mounts.is_empty() {
    mount_turrets(world, entity);
  }
  entity
}

/// Даёт готовому зданию то, что положено его виду помимо производства (`equip`).
pub fn equip(world: &mut World, entity: Entity, kind: &str) {
  let spec = &DATA.buildings[kind];
  if let Some(radius) = spec.repair.filter(|&radius| radius != 0.0) {
    add(world, entity, "Repair", vec![("radius", js_number(radius))]);
  }
  if spec.shield.is_some() {
    add(world, entity, "Shield", vec![]);
  }
  if spec.battery.is_some() {
    add(world, entity, "Accumulator", vec![]);
  }
  if spec.requester {
    add(world, entity, "Wanted", vec![]);
  }
  if spec.burns.is_some() {
    add(world, entity, "Reactor", vec![]);
  }
  if spec.warhead.is_some() {
    add(world, entity, "Silo", vec![]);
  }
  if spec.intercept.is_some() {
    add(world, entity, "Interceptor", vec![]);
  }
  if spec.produces.is_some() && spec.storage.inventory.is_none_or(|capacity| capacity == 0.0) {
    equip_storage(world, entity, &Storage { inventory: Some(PRODUCER_HOLD), ..Storage::default() });
  }
  equip_storage(world, entity, &spec.storage);
  if let Some(recipe) = &spec.assembles {
    add(world, entity, "Assembly", vec![("recipe", json!(recipe))]);
  }
  // Carrier — только у вида с турелями: пустых списков в данных нет.
  if !spec.mounts.is_empty() {
    mount_turrets(world, entity);
  }
}

/// Ставит здание без проверок; player — владелец (`placeBuilding`).
pub fn place_building(world: &mut World, kind: &str, x: f64, y: f64, player: f64, durability: f64) -> Entity {
  let phase = world.count("Building") as f64 * 5.0;
  let entity = spawn(
    world,
    vec![
      ("Position", make("Position", [("x", js_number(x)), ("y", js_number(y))])),
      ("Building", make("Building", [("type", json!(kind)), ("phase", js_number(phase))])),
      ("Health", make("Health", [("value", js_number(durability)), ("max", js_number(durability))])),
      ("Owner", make("Owner", [("player", js_number(player))])),
    ],
  );
  if DATA.buildings[kind].produces.is_some() && player != 0.0 {
    add(world, entity, "Producer", vec![]);
  }
  equip(world, entity, kind);
  entity
}
