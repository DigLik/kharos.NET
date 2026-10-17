//! Общее для систем (common.ts и соседи): чьё, когда очередь, турели носителей, оружие, союзы, защита новичка.

use crate::components;
use crate::data::{WeaponSpec, DATA};
use crate::math::js_hypot;
use crate::values::{entity, number, text};
use kharos_engine::{Entity, World};
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};

/// Цель «никто» (`NONE`).
pub const NONE: f64 = -1.0;

/// Пришла ли очередь сущности делать то, что делается раз в ticks тиков (`onTurn`).
pub fn on_turn(tick: u64, entity: Entity, ticks: u64) -> bool {
  (tick + entity as u64) % ticks == 0
}

/// Чья это сущность; ничья — игрок 0 (`ownerOf`).
pub fn owner_of(world: &World, entity: Entity) -> f64 {
  number(world, entity, "Owner", "player").unwrap_or(0.0)
}

/// Принадлежит ли сущность игроку (`isOwn`).
pub fn is_own(world: &World, player: f64, entity: Entity) -> bool {
  number(world, entity, "Owner", "player") == Some(player)
}

/// Расстояние от точки до прямоугольника; внутри — ноль (`rectDistance`).
pub fn rect_distance(left: f64, top: f64, width: f64, height: f64, x: f64, y: f64) -> f64 {
  js_hypot((left - x).max(0.0).max(x - left - width), (top - y).max(0.0).max(y - top - height))
}

/// Турели носителя (`turretsOf`).
pub fn turrets_of(world: &World, entity: Entity) -> Vec<Entity> {
  let Some(turrets) = world.get(entity, "Carrier").and_then(|carrier| carrier.get("turrets")).and_then(Value::as_array) else { return Vec::new() };
  turrets.iter().filter_map(Value::as_f64).map(|turret| turret as Entity).collect()
}

/// Носитель турели; не турель — она сама (`carrierOf`).
pub fn carrier_of(world: &World, entity: Entity) -> Entity {
  match world.get(entity, "Attached").and_then(|attached| attached.get("parent")).and_then(Value::as_f64) {
    Some(parent) => parent as Entity,
    None => entity,
  }
}

/// Сущность и её турели.
pub fn gunners(world: &World, entity: Entity) -> Vec<Entity> {
  let mut all = vec![entity];
  all.extend(turrets_of(world, entity));
  all
}

/// Оружие юнита или турели (`weaponOf`).
pub fn weapon_of(world: &World, entity: Entity) -> Option<&'static str> {
  if let Some(kind) = text(world, entity, "Unit", "type") {
    return DATA.units.get(kind).and_then(|spec| spec.weapon.as_deref());
  }
  text(world, entity, "Turret", "type").and_then(|kind| DATA.turrets.get(kind)).and_then(|spec| spec.weapon.as_deref())
}

pub fn weapon(kind: &str) -> &'static WeaponSpec {
  &DATA.weapons[kind]
}

/// Враги ли игроки; игрок 0 ни с кем не воюет (`hostile`).
pub fn hostile(a: f64, b: f64) -> bool {
  a != b && a != 0.0 && b != 0.0
}

/// Союзы: игрок → его союзники (`alliances`).
pub fn alliances(world: &World) -> HashMap<u64, Vec<f64>> {
  let mut all = HashMap::new();
  for player in world.query(&["Player"]) {
    let allies: Vec<f64> = world.get(player, "Player").and_then(|info| info.get("allies")).and_then(Value::as_array).map(|list| list.iter().filter_map(Value::as_f64).collect()).unwrap_or_default();
    if !allies.is_empty() {
      all.insert(number(world, player, "Player", "id").unwrap_or(0.0) as u64, allies);
    }
  }
  all
}

/// Союзник ли other игроку player — по собранным на тик союзам.
pub fn friend(friends: &HashMap<u64, Vec<f64>>, player: f64, other: f64) -> bool {
  friends.get(&(player as u64)).is_some_and(|allies| allies.contains(&other))
}

/// Игроки под защитой новичка (`protectedPlayers`).
pub fn protected_players(world: &World, tick: u64) -> HashSet<u64> {
  let mut safe = HashSet::new();
  for player in world.query(&["Player"]) {
    if number(world, player, "Player", "safe").unwrap_or(0.0) > tick as f64 {
      safe.insert(number(world, player, "Player", "id").unwrap_or(0.0) as u64);
    }
  }
  safe
}

/// Прочность здания в единицах урона (`buildingHp`).
pub fn building_hp(kind: &str) -> f64 {
  let spec = &DATA.buildings[kind];
  spec.hp.unwrap_or(spec.cost * 2.0)
}

/// Открыты ли ворота (`isOpenGate`).
pub fn is_open_gate(world: &World, building: Entity) -> bool {
  world.get(building, "Gate").and_then(|gate| gate.get("open")).and_then(Value::as_bool) == Some(true) && !world.has(building, "Site")
}

/// Стена — или закрытые ворота (`isWall`).
pub fn is_wall(world: &World, building: Entity) -> bool {
  match text(world, building, "Building", "type") {
    Some("wall") => true,
    Some("gate") => !is_open_gate(world, building),
    _ => false,
  }
}

/// Стойка юнита: без тактики — оборона (`stanceOf`).
pub fn stance_of(world: &World, entity: Entity) -> &str {
  text(world, entity, "Tactics", "stance").unwrap_or("defensive")
}

/// Юнит уходит в погоню без приказа: запоминает, откуда (`leaveHome`).
pub fn leave_home(world: &mut World, entity: Entity) {
  components::add(world, entity, "Tactics", []);
  let tactics = world.get(entity, "Tactics").expect("тактика");
  let patrol = tactics.get("patrol").and_then(Value::as_array).is_some_and(|patrol| !patrol.is_empty());
  if tactics.get("away").and_then(Value::as_bool).unwrap_or(false) || patrol {
    return;
  }
  let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
  world.set(entity, "Tactics", [("away".to_owned(), json!(true)), ("homeX".to_owned(), crate::values::js_number(x.floor())), ("homeY".to_owned(), crate::values::js_number(y.floor()))].into_iter().collect());
}

/// Ссылка на сущность в поле; NONE — None.
pub fn target(world: &World, entity_: Entity, key: &str, field: &str) -> Option<Entity> {
  entity(world, entity_, key, field)
}

/// Сущность игрока по номеру (`playerEntity`): из нескольких — последняя появившаяся.
pub fn player_entity(world: &World, player: f64) -> Option<Entity> {
  world.query(&["Player"]).into_iter().filter(|&entity| number(world, entity, "Player", "id") == Some(player)).max()
}

/// Выдаёт игроку награду, если он её ещё не получал (`reward`).
pub fn reward(world: &mut World, player: f64, key: &str) {
  let (Some(entity), Some(prize)) = (player_entity(world, player), DATA.rewards.get(key)) else { return };
  let credits = number(world, entity, "Player", "credits").unwrap_or(0.0);
  let mut rewards: Vec<Value> = world.get(entity, "Player").and_then(|info| info.get("rewards")).and_then(Value::as_array).cloned().unwrap_or_default();
  if rewards.iter().any(|other| other.as_str() == Some(key)) {
    return;
  }
  rewards.push(json!(key));
  world.set(entity, "Player", [("credits".to_owned(), crate::values::js_number(credits + prize.credits)), ("rewards".to_owned(), Value::Array(rewards))].into_iter().collect());
}
