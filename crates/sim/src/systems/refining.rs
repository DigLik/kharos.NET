//! Переработка (refining.ts): завод превращает руду со своего склада в её ресурс, пока готовому есть куда лечь.

use crate::data::{level_factor, DATA};
use crate::inventory::Stock;
use crate::values::{number, text};
use crate::{Sim, Time};

/// Сколько руды переработка принимает в секунду.
pub const REFINE_RATE: f64 = 1.5;
/// Сколько руды уходит на единицу готового ресурса.
pub const REFINE_RATIO: f64 = 1.0;
const EPSILON: f64 = 1e-9;

/// Раз в тик (`refine`): при нехватке энергии завод работает медленнее, уровнем выше — быстрее.
pub fn refine(sim: &mut Sim, time: Time) {
  let world = &mut sim.world;
  let mut working = Vec::new();
  for entity in world.query(&["Building", "Inventory"]) {
    let Some(ore) = text(world, entity, "Building", "type").and_then(|kind| DATA.buildings.get(kind)).and_then(|spec| spec.refines.as_deref()) else { continue };
    if world.has(entity, "Site") {
      continue;
    }
    if Stock::of(world, entity).is_some_and(|stock| stock.amount(ore) > EPSILON) {
      working.push((entity, ore));
    }
  }
  for (entity, ore) in working {
    let Some(mut stock) = Stock::of(world, entity) else { continue };
    let Some(product) = DATA.ores.get(ore) else { continue };
    let level = number(world, entity, "Building", "level").unwrap_or(1.0);
    let wanted = (REFINE_RATE * level_factor(level) * time.step * sim.power.get(&entity).copied().unwrap_or(0.0)).min(stock.amount(ore));
    // Готовому некуда лечь — завод стоит, пока грузовики не развезут его.
    let taken = wanted.min(stock.room_for(product) * REFINE_RATIO);
    if taken > EPSILON {
      let ore_taken = stock.take(ore, taken);
      stock.put(product, ore_taken / REFINE_RATIO);
      stock.save(world, entity);
    }
  }
}
