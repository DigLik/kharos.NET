//! Дропы — груз на земле (drops.ts): брошенный к уже лежащему на тайле или новым дропом; под новым зданием они пропадают.

use crate::components::make;
use crate::data::DATA;
use crate::inventory::Stock;
use crate::spawning::spawn;
use crate::values::{js_number, number};
use kharos_engine::World;
use serde_json::{Map, Value};

const EPSILON: f64 = 1e-9;

/// Бросает груз на тайл: к дропу, что уже там, или новым (`dropItems`).
pub fn drop_items(world: &mut World, x: f64, y: f64, items: &Map<String, Value>) {
  let amount = |good: &str| items.get(good).and_then(Value::as_f64).unwrap_or(0.0);
  let goods: Vec<&String> = DATA.goods.iter().filter(|good| amount(good) > EPSILON).collect();
  if goods.is_empty() {
    return;
  }
  let (tile_x, tile_y) = (x.floor(), y.floor());
  let existing = world.query(&["Position", "Drop"]).into_iter().find(|&drop| number(world, drop, "Position", "x") == Some(tile_x) && number(world, drop, "Position", "y") == Some(tile_y));
  let drop = existing.unwrap_or_else(|| spawn(world, vec![("Position", make("Position", [("x", js_number(tile_x)), ("y", js_number(tile_y))])), ("Drop", make("Drop", [])), ("Inventory", make("Inventory", []))]));
  let mut stock = Stock::of(world, drop).expect("склад дропа");
  for good in goods {
    let value = stock.amount(good) + amount(good);
    stock.items.insert(good.clone(), js_number(value));
  }
  let capacity = stock.load();
  let items = stock.items;
  world.set(drop, "Inventory", Map::from_iter([("items".to_owned(), Value::Object(items)), ("capacity".to_owned(), js_number(capacity))]));
}

/// Уничтожает дропы под основанием (`clearDrops`).
pub fn clear_drops(world: &mut World, x: f64, y: f64, width: f64, height: f64) {
  let mut gone = Vec::new();
  for entity in world.query(&["Position", "Drop"]) {
    let (px, py) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    if px >= x && px < x + width && py >= y && py < y + height {
      gone.push(entity);
    }
  }
  for entity in gone {
    world.destroy(entity);
  }
}
