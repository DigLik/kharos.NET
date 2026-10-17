//! Компоненты по умолчанию (components.ts): системы на Rust, создавая сущность или добавляя компонент, получают те же
//! данные, что `Component.create` в TS, — с теми же полями в том же порядке. Таблицу с TS сверяет
//! packages/tests/components.test.ts.

use kharos_engine::{Entity, World};
use serde_json::{Map, Value};
use std::sync::LazyLock;

static DEFAULTS: LazyLock<Map<String, Value>> = LazyLock::new(|| serde_json::from_str(include_str!("components.json")).expect("components.json"));

/// Данные компонента key: значения по умолчанию, поверх — fields (`Component.create(init)`). Метка — `{}`.
pub fn make(key: &str, fields: impl IntoIterator<Item = (&'static str, Value)>) -> Value {
  let Some(Value::Object(defaults)) = DEFAULTS.get(key) else { panic!("нет компонента {key}") };
  let mut data = defaults.clone();
  if !data.is_empty() {
    for (name, value) in fields {
      data.insert(name.to_owned(), value);
    }
  }
  Value::Object(data)
}

/// Добавляет компонент со значениями по умолчанию и fields поверх; есть — правит поля (`world.add` в TS).
pub fn add(world: &mut World, entity: Entity, key: &str, fields: impl IntoIterator<Item = (&'static str, Value)>) {
  if world.has(entity, key) {
    world.set(entity, key, fields.into_iter().map(|(name, value)| (name.to_owned(), value)).collect());
  } else {
    world.add(entity, key, make(key, fields));
  }
}

#[cfg(test)]
mod tests {
  use super::*;
  use serde_json::json;

  #[test]
  fn fields_keep_their_places() {
    let path = make("Path", [("goalY", json!(4)), ("points", json!([1.5, 2.5]))]);
    let keys: Vec<_> = path.as_object().unwrap().keys().cloned().collect();
    assert_eq!(keys[..3], ["points", "goalX", "goalY"]);
    assert_eq!(path["goalY"], 4);
    assert_eq!(make("Drop", [("x", json!(1))]), json!({}));
  }
}
