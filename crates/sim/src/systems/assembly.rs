//! Сборочные цеха (assembly.ts): сборка начинается, когда на складе есть сырьё на неё и место под готовое, — тогда сырьё
//! сразу уходит в работу; при нехватке энергии сборка идёт медленнее.

use crate::data::DATA;
use crate::inventory::Stock;
use crate::values::{number, patch, text};
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::Value;

/// Сколько единиц сырья цех перерабатывает в секунду, всех видов вместе.
pub const ASSEMBLE_RATE: f64 = 1.0;
const EPSILON: f64 = 1e-9;

/// Сколько секунд идёт одна сборка изделия (`cycleSeconds`).
pub fn cycle_seconds(product: &str) -> f64 {
  DATA.products.get(product).map_or(0.0, |spec| spec.total()) / ASSEMBLE_RATE
}

/// Есть ли у цеха место под готовое одной сборки (`hasRoom`).
pub fn has_room(stock: &Stock, product: &str) -> bool {
  stock.room_for(product) >= DATA.products.get(product).map_or(0.0, |spec| spec.output) - EPSILON
}

/// Можно ли начать сборку: сырьё на месте и под готовое есть место (`canStart`).
fn can_start(world: &World, entity: Entity, product: &str) -> bool {
  let Some(stock) = Stock::of(world, entity) else { return false };
  let Some(spec) = DATA.products.get(product) else { return false };
  has_room(&stock, product) && spec.recipe().all(|(resource, amount)| stock.amount(resource) >= amount - EPSILON)
}

/// Раз в тик: цеха собирают изделия (`assemble`).
pub fn assemble(sim: &mut Sim, time: Time) {
  let world = &mut sim.world;
  let mut working = Vec::new();
  for entity in world.query(&["Assembly", "Inventory"]) {
    if world.has(entity, "Site") {
      continue;
    }
    let product = text(world, entity, "Assembly", "recipe").unwrap_or_default();
    let on = world.get(entity, "Assembly").and_then(|assembly| assembly.get("on")).and_then(Value::as_bool).unwrap_or(false);
    if number(world, entity, "Assembly", "progress").unwrap_or(0.0) > 0.0 || (on && can_start(world, entity, product)) {
      working.push(entity);
    }
  }
  for entity in working {
    let product = text(world, entity, "Assembly", "recipe").unwrap_or_default().to_owned();
    let (Some(spec), Some(mut stock)) = (DATA.products.get(&product), Stock::of(world, entity)) else { continue };
    let mut progress = number(world, entity, "Assembly", "progress").unwrap_or(0.0);
    if progress <= 0.0 {
      for (resource, amount) in spec.recipe() {
        stock.take(resource, amount);
      }
      // Сырьё ушло в работу — сборка начата, даже если энергии сейчас нет.
      progress = EPSILON;
    }
    let cycle = cycle_seconds(&product);
    progress = cycle.min(progress + time.step * sim.power.get(&entity).copied().unwrap_or(0.0));
    // Готовому некуда лечь — сборка ждёт, пока грузовики не освободят склад.
    if progress >= cycle - EPSILON && has_room(&stock, &product) {
      stock.put(&product, spec.output);
      progress = 0.0;
    }
    stock.save(world, entity);
    world.set(entity, "Assembly", patch([("progress", progress)]));
  }
}
