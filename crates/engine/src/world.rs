//! Мир: сущности и их компоненты. Повторяет TS-мир (`packages/ecs/src/world.ts`) до порядка обхода: номера сущностей,
//! плотные массивы на компонент, удаление перестановкой последнего, обход от самого маленького хранилища. Так одни и те
//! же операции в одном порядке дают на обеих сторонах один мир, и системы обходят его одинаково (docs/boundary.md, §3).
//!
//! Данные компонента пока — JSON-значение, как в сохранении и в сети: хранилищу их смысл не нужен. Типы появятся у
//! компонента, когда его начнёт читать система на Rust.

use serde_json::{Map, Value};
use std::collections::{BTreeSet, HashMap};

/// Сущность — просто номер. Номера не переиспользуются.
pub type Entity = u32;

/// Операция над миром, как её пишет журнал: создание и уничтожение сущности, добавление, изменение (только поля из
/// patch) и снятие компонента, сдвиг номера следующей сущности. Те же операции, что `JournalOp` в TS.
#[derive(Clone, Debug, PartialEq)]
pub enum Op {
  Spawn(Entity),
  Destroy(Entity),
  Add(Entity, String, Value),
  Set(Entity, String, Map<String, Value>),
  Remove(Entity, String),
  Next(Entity),
}

impl Op {
  /// Из записи журнала TS: `["add", 3, "Position", {"x":1,"y":2}]`.
  pub fn from_json(value: &Value) -> Result<Op, String> {
    let list = value.as_array().ok_or("операция — не список")?;
    let name = list.first().and_then(Value::as_str).ok_or("нет имени операции")?;
    let number = |at: usize| list.get(at).and_then(Value::as_u64).map(|n| n as Entity).ok_or_else(|| format!("{name}: нет номера"));
    let key = || list.get(2).and_then(Value::as_str).map(str::to_owned).ok_or_else(|| format!("{name}: нет имени компонента"));
    Ok(match name {
      "spawn" => Op::Spawn(number(1)?),
      "destroy" => Op::Destroy(number(1)?),
      "next" => Op::Next(number(1)?),
      "add" => Op::Add(number(1)?, key()?, list.get(3).cloned().ok_or("add: нет данных")?),
      "set" => Op::Set(number(1)?, key()?, list.get(3).and_then(Value::as_object).cloned().ok_or("set: поля — не объект")?),
      "remove" => Op::Remove(number(1)?, key()?),
      other => return Err(format!("неизвестная операция {other}")),
    })
  }

  /// Запись журнала для TS, в той же форме, что `from_json`.
  pub fn to_json(&self) -> Value {
    match self {
      Op::Spawn(entity) => Value::from(vec![Value::from("spawn"), Value::from(*entity)]),
      Op::Destroy(entity) => Value::from(vec![Value::from("destroy"), Value::from(*entity)]),
      Op::Next(next) => Value::from(vec![Value::from("next"), Value::from(*next)]),
      Op::Add(entity, key, data) => Value::from(vec![Value::from("add"), Value::from(*entity), Value::from(key.as_str()), data.clone()]),
      Op::Set(entity, key, patch) => Value::from(vec![Value::from("set"), Value::from(*entity), Value::from(key.as_str()), Value::Object(patch.clone())]),
      Op::Remove(entity, key) => Value::from(vec![Value::from("remove"), Value::from(*entity), Value::from(key.as_str())]),
    }
  }
}

/// Хранилище одного компонента: плотные массивы сущностей и данных плюс индекс «сущность → место».
#[derive(Default)]
struct Store {
  key: String,
  entities: Vec<Entity>,
  data: Vec<Value>,
  index: HashMap<Entity, usize>,
}

impl Store {
  fn insert(&mut self, entity: Entity, value: Value) {
    self.index.insert(entity, self.entities.len());
    self.entities.push(entity);
    self.data.push(value);
  }

  /// На место удалённого встаёт последний: массивы остаются плотными, как в TS.
  fn remove(&mut self, entity: Entity) {
    let Some(at) = self.index.remove(&entity) else { return };
    let last = self.entities.len() - 1;
    if at != last {
      let moved = self.entities[last];
      self.entities[at] = moved;
      self.data.swap(at, last);
      self.index.insert(moved, at);
    }
    self.entities.pop();
    self.data.pop();
  }

  fn get(&self, entity: Entity) -> Option<&Value> {
    self.index.get(&entity).map(|&at| &self.data[at])
  }
}

#[derive(Default)]
pub struct World {
  next: Entity,
  /// Живые сущности по возрастанию номера: в TS это порядок вставки, а номера в мир симуляции приходят по возрастанию.
  entities: BTreeSet<Entity>,
  /// Хранилища в порядке, в каком их ключи впервые встретились; по ключу — место.
  stores: Vec<Store>,
  by_key: HashMap<String, usize>,
  /// Журнал своих операций — пока он включён (record).
  journal: Option<Vec<Op>>,
}

impl World {
  pub fn new() -> Self {
    Self { next: 1, ..Default::default() }
  }

  /// Включает или выключает журнал своих операций; выключение его очищает.
  pub fn record(&mut self, on: bool) {
    self.journal = if on { Some(self.journal.take().unwrap_or_default()) } else { None };
  }

  /// Операции с прошлого раза, по порядку.
  pub fn take_journal(&mut self) -> Vec<Op> {
    self.journal.as_mut().map(std::mem::take).unwrap_or_default()
  }

  fn log(&mut self, op: impl FnOnce() -> Op) {
    if let Some(journal) = &mut self.journal {
      journal.push(op());
    }
  }

  /// Номер, который получит следующая новая сущность.
  pub fn next(&self) -> Entity {
    self.next
  }

  pub fn size(&self) -> usize {
    self.entities.len()
  }

  pub fn alive(&self, entity: Entity) -> bool {
    self.entities.contains(&entity)
  }

  pub fn spawn(&mut self) -> Entity {
    let entity = self.next;
    self.insert(entity);
    entity
  }

  /// Заводит сущность под данным номером, как `insert` в TS.
  fn insert(&mut self, entity: Entity) {
    if !self.entities.insert(entity) {
      return;
    }
    if entity >= self.next {
      self.next = entity + 1;
    }
    self.log(|| Op::Spawn(entity));
  }

  /// Уничтожает сущность со всеми компонентами: сначала снимаются компоненты — в порядке хранилищ, — потом она сама.
  pub fn destroy(&mut self, entity: Entity) {
    if !self.entities.contains(&entity) {
      return;
    }
    for at in 0..self.stores.len() {
      if self.stores[at].index.contains_key(&entity) {
        let key = self.stores[at].key.clone();
        self.remove(entity, &key);
      }
    }
    self.entities.remove(&entity);
    self.log(|| Op::Destroy(entity));
  }

  fn store_mut(&mut self, key: &str) -> &mut Store {
    let at = match self.by_key.get(key) {
      Some(&at) => at,
      None => {
        self.stores.push(Store { key: key.to_owned(), ..Default::default() });
        self.by_key.insert(key.to_owned(), self.stores.len() - 1);
        self.stores.len() - 1
      }
    };
    &mut self.stores[at]
  }

  fn store(&self, key: &str) -> Option<&Store> {
    self.by_key.get(key).map(|&at| &self.stores[at])
  }

  /// Добавляет компонент с данными; если он уже есть, данные ложатся поверх, как set.
  pub fn add(&mut self, entity: Entity, key: &str, data: Value) {
    if !self.entities.contains(&entity) {
      return;
    }
    if self.has(entity, key) {
      if let Value::Object(patch) = data {
        self.set(entity, key, patch);
      }
      return;
    }
    let logged = self.journal.is_some().then(|| data.clone());
    self.store_mut(key).insert(entity, data);
    if let Some(data) = logged {
      self.log(|| Op::Add(entity, key.to_owned(), data));
    }
  }

  /// Меняет поля компонента; те же значения — ничего не происходит. Компонента нет — добавляет его с этими полями.
  pub fn set(&mut self, entity: Entity, key: &str, patch: Map<String, Value>) {
    let Some(current) = self.get_mut(entity, key) else {
      self.add(entity, key, Value::Object(patch));
      return;
    };
    let Value::Object(fields) = current else { return };
    if patch.iter().all(|(name, value)| fields.get(name) == Some(value)) {
      return;
    }
    for (name, value) in &patch {
      fields.insert(name.clone(), value.clone());
    }
    self.log(|| Op::Set(entity, key.to_owned(), patch));
  }

  /// Сообщает журналу, что данные компонента поменяли на месте (get_mut): в журнал уходит всё значение.
  pub fn touch(&mut self, entity: Entity, key: &str) {
    if self.journal.is_none() {
      return;
    }
    if let Some(Value::Object(fields)) = self.get(entity, key) {
      let patch = fields.clone();
      self.log(|| Op::Set(entity, key.to_owned(), patch));
    }
  }

  pub fn remove(&mut self, entity: Entity, key: &str) {
    if !self.has(entity, key) {
      return;
    }
    self.store_mut(key).remove(entity);
    self.log(|| Op::Remove(entity, key.to_owned()));
  }

  pub fn has(&self, entity: Entity, key: &str) -> bool {
    self.store(key).is_some_and(|store| store.index.contains_key(&entity))
  }

  pub fn get(&self, entity: Entity, key: &str) -> Option<&Value> {
    self.store(key).and_then(|store| store.get(entity))
  }

  /// Данные для правки на месте; после правки — touch, иначе журнал её не увидит.
  pub fn get_mut(&mut self, entity: Entity, key: &str) -> Option<&mut Value> {
    let &at = self.by_key.get(key)?;
    let store = &mut self.stores[at];
    let &place = store.index.get(&entity)?;
    Some(&mut store.data[place])
  }

  /// Сколько сущностей с этим компонентом.
  pub fn count(&self, key: &str) -> usize {
    self.store(key).map_or(0, |store| store.entities.len())
  }

  /// Сущности с этими компонентами — в том порядке, в каком их обошёл бы запрос TS: по самому маленькому хранилищу
  /// (из равных — по первому в списке), с проверкой остальных.
  pub fn query(&self, keys: &[&str]) -> Vec<Entity> {
    let mut stores = Vec::with_capacity(keys.len());
    for key in keys {
      match self.store(key) {
        Some(store) => stores.push(store),
        None => return Vec::new(),
      }
    }
    let Some(driver) = stores.iter().copied().reduce(|best, store| if store.entities.len() < best.entities.len() { store } else { best }) else { return Vec::new() };
    driver.entities.iter().copied().filter(|entity| stores.iter().all(|store| store.index.contains_key(entity))).collect()
  }

  /// Повторяет операцию чужого журнала. В свой журнал она не пишется: та сторона о ней уже знает.
  pub fn replay(&mut self, op: Op) {
    let journal = self.journal.take();
    self.apply(op);
    self.journal = journal;
  }

  fn apply(&mut self, op: Op) {
    match op {
      Op::Spawn(entity) => self.insert(entity),
      Op::Destroy(entity) => self.destroy(entity),
      Op::Next(next) => {
        if next > self.next {
          self.next = next;
          self.log(|| Op::Next(next));
        }
      }
      Op::Add(entity, key, data) => self.add(entity, &key, data),
      Op::Set(entity, key, patch) => self.set(entity, &key, patch),
      Op::Remove(entity, key) => self.remove(entity, &key),
    }
  }

  /// Снимок в форме `WorldSnapshot` TS: `{ next, entities: [[id, { key: data }]] }`. Сущности без компонентов в него не
  /// попадают, как и в TS; компоненты — в порядке хранилищ.
  pub fn snapshot(&self) -> Value {
    let mut entities = Vec::new();
    for &entity in &self.entities {
      let mut data = Map::new();
      for store in &self.stores {
        if let Some(value) = store.get(entity) {
          data.insert(store.key.clone(), value.clone());
        }
      }
      if !data.is_empty() {
        entities.push(Value::from(vec![Value::from(entity), Value::Object(data)]));
      }
    }
    let mut snapshot = Map::new();
    snapshot.insert("next".into(), Value::from(self.next));
    snapshot.insert("entities".into(), Value::from(entities));
    Value::Object(snapshot)
  }
}

#[cfg(test)]
mod tests {
  use super::*;
  use serde_json::json;

  fn fields(value: Value) -> Map<String, Value> {
    value.as_object().unwrap().clone()
  }

  #[test]
  fn removal_moves_the_last_into_the_gap_like_ts() {
    let mut world = World::new();
    let ids: Vec<Entity> = (0..4).map(|_| world.spawn()).collect();
    for &entity in &ids {
      world.add(entity, "Position", json!({ "x": entity, "y": 0 }));
    }
    world.remove(ids[0], "Position");
    // В TS на место первого встаёт последний: 4, 2, 3.
    assert_eq!(world.query(&["Position"]), vec![4, 2, 3]);
  }

  #[test]
  fn query_goes_by_the_smallest_store() {
    let mut world = World::new();
    for _ in 0..3 {
      let entity = world.spawn();
      world.add(entity, "Position", json!({}));
    }
    world.add(3, "Velocity", json!({}));
    world.add(1, "Velocity", json!({}));
    assert_eq!(world.query(&["Position", "Velocity"]), vec![3, 1]);
    assert_eq!(world.query(&["Position", "Missing"]), Vec::<Entity>::new());
  }

  #[test]
  fn journal_replays_into_the_same_world() {
    let mut world = World::new();
    world.record(true);
    let a = world.spawn();
    let b = world.spawn();
    world.add(a, "Health", json!({ "value": 1, "max": 1 }));
    world.add(b, "Health", json!({ "value": 1, "max": 1 }));
    world.set(a, "Health", fields(json!({ "value": 0.5 })));
    world.set(a, "Health", fields(json!({ "value": 0.5 })));
    if let Some(Value::Object(health)) = world.get_mut(b, "Health") {
      health.insert("value".into(), json!(0.25));
    }
    world.touch(b, "Health");
    world.destroy(a);
    let journal = world.take_journal();
    // Повтор того же значения — не изменение.
    assert_eq!(journal.iter().filter(|op| matches!(op, Op::Set(..))).count(), 2);
    let mut copy = World::new();
    for op in journal {
      copy.replay(Op::from_json(&op.to_json()).unwrap());
    }
    assert_eq!(copy.snapshot(), world.snapshot());
    assert_eq!(copy.spawn(), world.spawn());
  }
}
