//! Корабли с подарками союзникам (ferry.ts, `ferry`): долетев, отдают получателю деньги, а груз — в его хранилища;
//! что не вошло, падает на землю у его главного здания.

use crate::drops::drop_items;
use crate::economy::add_credits;
use crate::inventory::Stock;
use crate::logistics::is_store;
use crate::values::{js_number, number};
use crate::{Sim, Time};
use serde_json::{Map, Value};

/// Кладёт груз в хранилища всех зон игрока; отдаёт, сколько вошло (`putIntoStores`).
fn put_into_stores(sim: &mut Sim, player: f64, good: &str, amount: f64) -> f64 {
  let buildings: Vec<_> = sim.zones.of_player(&sim.world, &sim.occupancy, &sim.layers, player).iter().flat_map(|zone| zone.buildings.clone()).collect();
  let mut left = amount;
  for entity in buildings {
    if !is_store(&sim.world, entity) {
      continue;
    }
    let Some(mut stock) = Stock::of(&sim.world, entity) else { continue };
    left -= stock.put(good, left);
    stock.save(&mut sim.world, entity);
    if left <= 0.0 {
      return amount;
    }
  }
  amount - left
}

/// Раз в тик: корабли с подарками долетают и выгружаются.
pub fn ferry(sim: &mut Sim, _time: Time) {
  // Что не вошло в хранилища, падает на землю после обхода.
  let mut spills = Vec::new();
  for entity in sim.world.query(&["Transfer"]) {
    let world = &mut sim.world;
    let transfer = world.get(entity, "Transfer").cloned().unwrap_or(Value::Null);
    if transfer.get("done").and_then(Value::as_bool).unwrap_or(false) {
      continue;
    }
    let left = transfer.get("left").and_then(Value::as_f64).unwrap_or(0.0) - 1.0;
    world.set(entity, "Transfer", Map::from_iter([("left".to_owned(), js_number(left))]));
    if left > 0.0 {
      continue;
    }
    let to = transfer.get("to").and_then(Value::as_f64).unwrap_or(0.0);
    let credits = transfer.get("credits").and_then(Value::as_f64).unwrap_or(0.0);
    if credits != 0.0 {
      add_credits(world, to, credits);
    }
    // Груз идёт в хранилища получателя; то, что не вошло, падает на землю у его главного здания.
    let mut spill = Map::new();
    for (good, amount) in transfer.get("goods").and_then(Value::as_object).cloned().unwrap_or_default() {
      let amount = amount.as_f64().unwrap_or(0.0);
      let stored = put_into_stores(sim, to, &good, amount);
      if amount - stored >= 1e-9 {
        spill.insert(good, js_number(amount - stored));
      }
    }
    if !spill.is_empty() {
      spills.push((number(&sim.world, entity, "Transfer", "x").unwrap_or(0.0), number(&sim.world, entity, "Transfer", "y").unwrap_or(0.0), spill));
    }
    sim.world.set(entity, "Transfer", Map::from_iter([("credits".to_owned(), js_number(0.0)), ("goods".to_owned(), Value::Object(Map::new())), ("done".to_owned(), Value::Bool(true))]));
  }
  for (x, y, items) in spills {
    drop_items(&mut sim.world, x, y, &items);
  }
}
