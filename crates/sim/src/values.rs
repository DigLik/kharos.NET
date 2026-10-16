//! Чтение и запись полей компонентов, пока они JSON-значения (crates/engine, world.rs).

use kharos_engine::{Entity, World};
use serde_json::{Map, Value};

/// Число из поля компонента; нет компонента или поля — None.
pub fn number(world: &World, entity: Entity, key: &str, field: &str) -> Option<f64> {
  world.get(entity, key)?.get(field)?.as_f64()
}

/// Строка из поля компонента.
pub fn text<'a>(world: &'a World, entity: Entity, key: &str, field: &str) -> Option<&'a str> {
  world.get(entity, key)?.get(field)?.as_str()
}

/// Номер сущности из поля-ссылки; -1 (NONE в TS) и всё отрицательное — None.
pub fn entity(world: &World, entity: Entity, key: &str, field: &str) -> Option<Entity> {
  let value = number(world, entity, key, field)?;
  (value >= 0.0).then_some(value as Entity)
}

/// Число как его пишет JS: целое — целым (`3`, а не `3.0`; `-0` — `0`). Так значение совпадает с тем, что пришло из TS,
/// и `World::set` не принимает то же число за новое.
pub fn js_number(value: f64) -> Value {
  if value.fract() == 0.0 && value.abs() < 9007199254740992.0 {
    Value::from(value as i64)
  } else {
    Value::from(value)
  }
}

/// Поля для World::set.
pub fn patch<const N: usize>(fields: [(&str, f64); N]) -> Map<String, Value> {
  fields.into_iter().map(|(name, value)| (name.to_owned(), js_number(value))).collect()
}
