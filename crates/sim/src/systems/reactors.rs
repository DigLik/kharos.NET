//! Реакторы (reactors.ts): включённый жжёт стержень за стержнем со своего склада и, пока горит, вырабатывает энергию.

use crate::data::DATA;
use crate::math::js_round;
use crate::values::{number, patch, text};
use crate::Time;
use kharos_engine::World;
use serde_json::{Map, Value};

/// Раз в тик, до энергии зон: реакторы жгут стержни и берут следующий со склада, как только догорит нынешний
/// (`burnFuel`). Стержень в работе у выключенного не тает: выключатель ставит работу на паузу.
pub fn burn_fuel(world: &mut World, time: Time) {
  for entity in world.query(&["Reactor", "Building", "Inventory"]) {
    if world.has(entity, "Off") {
      continue;
    }
    let mut left = number(world, entity, "Reactor", "left").unwrap_or(0.0);
    if left > 0.0 {
      left -= 1.0;
      world.set(entity, "Reactor", patch([("left", left)]));
    }
    if left > 0.0 {
      continue;
    }
    let Some(burns) = text(world, entity, "Building", "type").and_then(|kind| DATA.buildings.get(kind)).and_then(|spec| spec.burns.as_ref()) else { continue };
    let Some(mut items) = world.get(entity, "Inventory").and_then(|inventory| inventory.get("items")).and_then(Value::as_object).cloned() else { continue };
    let rods = items.get(&burns.good).and_then(Value::as_f64).unwrap_or(0.0);
    if rods < 1.0 {
      continue;
    }
    items.insert(burns.good.clone(), crate::values::js_number(rods - 1.0));
    world.set(entity, "Inventory", Map::from_iter([("items".to_owned(), Value::Object(items))]));
    world.set(entity, "Reactor", patch([("left", js_round(burns.seconds / time.step))]));
  }
}
