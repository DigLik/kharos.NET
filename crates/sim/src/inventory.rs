//! Склады (inventory.ts): что лежит, сколько поместится, положить и забрать. Склад читается из компонента `Inventory`
//! целиком и пишется обратно своим `items` — в том же порядке ключей, что у TS: новый груз ложится в конец.

use crate::data::DATA;
use crate::values::js_number;
use kharos_engine::{Entity, World};
use serde_json::{Map, Value};

pub struct Stock {
  pub items: Map<String, Value>,
  pub capacity: f64,
  pub accepts: Vec<String>,
  pub limits: Map<String, Value>,
  pub reserve: f64,
}

impl Stock {
  /// Склад сущности; нет — None.
  pub fn of(world: &World, entity: Entity) -> Option<Stock> {
    let inventory = world.get(entity, "Inventory")?;
    let object = |name| inventory.get(name).and_then(Value::as_object).cloned().unwrap_or_default();
    let number = |name| inventory.get(name).and_then(Value::as_f64).unwrap_or(0.0);
    let accepts = inventory.get("accepts").and_then(Value::as_array).map(|list| list.iter().filter_map(|good| good.as_str().map(str::to_owned)).collect()).unwrap_or_default();
    Some(Stock { items: object("items"), capacity: number("capacity"), accepts, limits: object("limits"), reserve: number("reserve") })
  }

  /// Пишет груз склада обратно в мир.
  pub fn save(self, world: &mut World, entity: Entity) {
    world.set(entity, "Inventory", Map::from_iter([("items".to_owned(), Value::Object(self.items))]));
  }

  /// Сколько этого груза лежит (`amountOf`).
  pub fn amount(&self, good: &str) -> f64 {
    self.items.get(good).and_then(Value::as_f64).unwrap_or(0.0)
  }

  /// Сколько лежит всего, вместе с рудой (`loadOf`): сумма по грузам в порядке GOODS — так же, как в TS, до знака.
  pub fn load(&self) -> f64 {
    DATA.goods.iter().fold(0.0, |sum, good| sum + self.amount(good))
  }

  /// Сколько этого груза ещё поместится (`roomFor`).
  pub fn room_for(&self, good: &str) -> f64 {
    if !self.accepts.is_empty() && !self.accepts.iter().any(|other| other == good) {
      return 0.0;
    }
    let left = self.limits.get(good).and_then(Value::as_f64).map_or(f64::INFINITY, |limit| limit - self.amount(good));
    0f64.max(left.min(self.capacity - self.load()))
  }

  fn write(&mut self, good: &str, value: f64) {
    self.items.insert(good.to_owned(), js_number(value));
  }

  /// Кладёт до amount груза, сколько поместится; отдаёт, сколько положено (`put`).
  pub fn put(&mut self, good: &str, amount: f64) -> f64 {
    let moved = 0f64.max(amount.min(self.room_for(good)));
    if moved != 0.0 {
      self.write(good, self.amount(good) + moved);
    }
    moved
  }

  /// Забирает до amount груза, сколько есть; отдаёт, сколько забрано (`take`).
  pub fn take(&mut self, good: &str, amount: f64) -> f64 {
    let moved = 0f64.max(amount.min(self.amount(good)));
    if moved != 0.0 {
      self.write(good, self.amount(good) - moved);
    }
    moved
  }
}

#[cfg(test)]
mod tests {
  use super::*;
  use serde_json::json;

  fn stock(inventory: Value) -> Stock {
    let mut world = World::new();
    let entity = world.spawn();
    world.add(entity, "Inventory", inventory);
    Stock::of(&world, entity).unwrap()
  }

  #[test]
  fn room_follows_accepts_limits_and_capacity() {
    let mut stock = stock(json!({ "items": { "metal": 3 }, "capacity": 10, "accepts": ["metal", "silicon"], "limits": { "silicon": 4 }, "reserve": 0 }));
    assert_eq!(stock.room_for("fuel"), 0.0);
    assert_eq!(stock.room_for("metal"), 7.0);
    assert_eq!(stock.room_for("silicon"), 4.0);
    assert_eq!(stock.put("silicon", 6.0), 4.0);
    assert_eq!(stock.room_for("metal"), 3.0);
    assert_eq!(stock.take("metal", 5.0), 3.0);
    // Новый груз — в конец, взятый до нуля остаётся нулём: так, как пишет TS.
    assert_eq!(serde_json::to_string(&stock.items).unwrap(), r#"{"metal":0,"silicon":4}"#);
  }
}
