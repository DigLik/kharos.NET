//! Работающие потребители энергии (busy.ts, `markBusy`): здание, у которого есть дело, получает метку Busy и просит у
//! зоны всю свою энергию; кончив дело, ещё HOLD секунд значится работающим.

use super::assembly::{cycle_seconds, has_room};
use super::production::missing_for;
use crate::common::{turrets_of, NONE};
use crate::components;
use crate::data::{BuildingSpec, DATA};
use crate::economy::{always_busy, switched_off};
use crate::inventory::Stock;
use crate::values::{number, text};
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::Value;
use std::collections::HashSet;

/// Раз во сколько тиков решается, кто работает (`BUSY_TICKS`).
pub const BUSY_TICKS: u64 = 10;
/// Сколько секунд здание значится работающим после того, как кончило дело.
const HOLD: f64 = 2.0;
const EPSILON: f64 = 1e-6;

fn flag(world: &World, entity: Entity, key: &str, field: &str) -> bool {
  world.get(entity, key).and_then(|data| data.get(field)).and_then(Value::as_bool).unwrap_or(false)
}

/// Есть ли у готового включённого потребителя дело. Вид, о котором тут ничего не сказано, занят всегда (`working`).
fn working(world: &World, entity: Entity, spec: &BuildingSpec) -> bool {
  // Переработка: есть руда и место под готовое.
  if let Some(ore) = &spec.refines {
    let product = DATA.ores.get(ore).map_or("", String::as_str);
    return Stock::of(world, entity).is_some_and(|stock| stock.amount(ore) > EPSILON && stock.room_for(product) > EPSILON);
  }
  // Цех: сборка идёт или её есть из чего начать; готовое, которому некуда лечь, — простой.
  if world.has(entity, "Assembly") {
    let product = text(world, entity, "Assembly", "recipe").unwrap_or_default();
    let stock = Stock::of(world, entity);
    let room = stock.as_ref().is_some_and(|stock| has_room(stock, product));
    let progress = number(world, entity, "Assembly", "progress").unwrap_or(0.0);
    if progress > 0.0 {
      return progress < cycle_seconds(product) - EPSILON || room;
    }
    let Some(stock) = stock else { return false };
    return room && DATA.products.get(product).is_some_and(|spec| spec.recipe().all(|(good, amount)| stock.amount(good) >= amount - EPSILON));
  }
  // Производство: заказ начат или его материалы на месте.
  if spec.produces.is_some() {
    let queued = world.get(entity, "Producer").and_then(|producer| producer.get("queue")).and_then(Value::as_array).is_some_and(|queue| !queue.is_empty());
    if !queued || world.has(entity, "Converting") {
      return false;
    }
    return number(world, entity, "Producer", "progress").unwrap_or(0.0) > 0.0 || missing_for(world, entity).is_empty();
  }
  // Лаборатория: ведёт тему или вела бы, будь энергия.
  if spec.researches {
    return world.has(entity, "Research") && (number(world, entity, "Research", "speed").unwrap_or(0.0) > 0.0 || text(world, entity, "Research", "lack") == Some("power"));
  }
  if spec.repair.is_some_and(|radius| radius != 0.0) {
    return number(world, entity, "Repair", "target").unwrap_or(NONE) != NONE;
  }
  if spec.warhead.is_some() {
    return world.has(entity, "Silo") && ((number(world, entity, "Silo", "ticks").unwrap_or(0.0) > 0.0 && !flag(world, entity, "Silo", "ready")) || number(world, entity, "Silo", "start").unwrap_or(0.0) >= 0.0);
  }
  if spec.intercept.is_some() {
    return number(world, entity, "Interceptor", "ticks").unwrap_or(0.0) > 0.0;
  }
  // Космопорт: идёт рейс или подарок союзнику.
  if spec.trades {
    return world.has(entity, "Trade") || world.has(entity, "Transfer");
  }
  // Щит: купол не полон — заряжается.
  if let Some(shield) = &spec.shield {
    return number(world, entity, "Shield", "charge").unwrap_or(0.0) < shield.capacity - EPSILON;
  }
  // Турель на энергии: есть цель или оружие ещё перезаряжается.
  if !spec.mounts.is_empty() {
    return turrets_of(world, entity).into_iter().any(|turret| world.has(turret, "Armed") && (number(world, turret, "Armed", "target").unwrap_or(NONE) != NONE || number(world, turret, "Armed", "cooldown").unwrap_or(0.0) > 0.0));
  }
  true
}

/// Раз в тик, до энергии зон: потребители, у которых есть дело, получают метку Busy, оставшиеся без него — теряют.
pub fn mark_busy(sim: &mut Sim, time: Time) {
  if !time.tick.is_multiple_of(BUSY_TICKS) {
    return;
  }
  let world = &sim.world;
  let hold = HOLD / time.step;
  let mut changed = Vec::new();
  let mut alive = HashSet::new();
  for entity in world.query(&["Building"]) {
    let spec = &DATA.buildings[text(world, entity, "Building", "type").unwrap_or_default()];
    // Кто работает всегда, тому метка не нужна: хозяйство знает это по виду здания.
    if spec.power.unwrap_or(0.0) >= 0.0 || always_busy(spec) {
      continue;
    }
    alive.insert(entity);
    let off = world.has(entity, "Site") || switched_off(world, entity);
    if !off && working(world, entity, spec) {
      sim.busy_seen.insert(entity, time.tick);
    }
    // Выключенному и недостроенному держать метку незачем.
    let since = if off { None } else { sim.busy_seen.get(&entity) };
    let busy = since.is_some_and(|&since| ((time.tick - since) as f64) < hold);
    if busy != world.has(entity, "Busy") {
      changed.push(entity);
    }
  }
  for entity in changed {
    if sim.world.has(entity, "Busy") {
      sim.world.remove(entity, "Busy");
    } else {
      components::add(&mut sim.world, entity, "Busy", Vec::new());
    }
  }
  sim.busy_seen.retain(|entity, _| alive.contains(entity));
}
