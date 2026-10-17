//! Хозяйство зоны (income.ts): сколько энергии зона даёт и просит, перегруз и доход. Хозяйства тика пока считает TS
//! (docs/boundary.md); здесь — то, что системы на Rust считают по зоне сами.

use crate::data::{level_factor, BuildingSpec, DATA};
use crate::values::{number, text};
use kharos_engine::{Entity, World};
use serde_json::Value;

/// Какую долю своей энергии просит потребитель без дела.
const IDLE_SHARE: f64 = 1.0 / 3.0;

/// Хозяйство зоны (`Economy`).
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Economy {
  pub produced: f64,
  pub demand: f64,
  pub peak: f64,
  pub spare: f64,
  pub income: f64,
  pub crowd: f64,
  pub stored: f64,
}

/// Сколько просит потребитель без дела (`idlePower`).
pub fn idle_power(power: f64) -> f64 {
  -(-power * IDLE_SHARE).ceil()
}

/// Занят ли вид всегда (`alwaysBusy`).
pub fn always_busy(spec: &BuildingSpec) -> bool {
  spec.income.is_some_and(|income| income != 0.0) || spec.scan.is_some_and(|scan| scan != 0.0)
}

/// Горит ли в реакторе стержень (`isBurning`).
pub fn is_burning(world: &World, entity: Entity) -> bool {
  number(world, entity, "Reactor", "left").unwrap_or(0.0) > 0.0 && !world.has(entity, "Off")
}

/// Выключил ли игрок потребителя (`switchedOff`): у цеха — свой флаг.
pub fn switched_off(world: &World, building: Entity) -> bool {
  match world.get(building, "Assembly") {
    Some(assembly) => !assembly.get("on").and_then(Value::as_bool).unwrap_or(false),
    None => world.has(building, "Off"),
  }
}

/// Сколько энергии даёт (больше нуля) или просит здание (`powerAt`).
pub fn power_at(world: &World, building: Entity) -> f64 {
  let spec = &DATA.buildings[text(world, building, "Building", "type").unwrap_or_default()];
  let level = number(world, building, "Building", "level").unwrap_or(1.0);
  let power = spec.power.unwrap_or(0.0);
  if spec.burns.is_some() && !is_burning(world, building) {
    return 0.0;
  }
  if power > 0.0 || spec.scan.is_some_and(|scan| scan != 0.0) {
    power * level_factor(level)
  } else {
    power
  }
}

/// Хозяйство зоны по её зданиям (`economyOfZone`); stored — считать ли отданное накопителями.
pub fn economy_of_zone(world: &World, buildings: &[Entity], step: f64, stored: bool) -> Economy {
  let mut economy = Economy::default();
  let core = buildings.iter().any(|&entity| text(world, entity, "Building", "type") == Some("command"));
  let mut powered = 0.0;
  for &entity in buildings {
    let kind = text(world, entity, "Building", "type").unwrap_or_default();
    let spec = &DATA.buildings[kind];
    let battery = if stored { world.get(entity, "Accumulator") } else { None };
    let flow = battery.and_then(|battery| battery.get("flow")).and_then(Value::as_f64).unwrap_or(0.0);
    if flow > 0.0 {
      economy.stored += flow;
    }
    if let (Some(battery), Some(spec)) = (battery, &spec.battery) {
      let charge = battery.get("charge").and_then(Value::as_f64).unwrap_or(0.0);
      economy.spare += 0f64.max((spec.rate - 0f64.max(flow)).min(charge / step));
    }
    let power = power_at(world, entity);
    let needs_core = kind != "command" && spec.income.is_some_and(|income| income != 0.0);
    let income = if core || !needs_core { spec.income.unwrap_or(0.0) } else { 0.0 };
    if power < 0.0 && switched_off(world, entity) {
      continue;
    }
    if power > 0.0 {
      economy.produced += power * number(world, entity, "Health", "value").unwrap_or(1.0);
    }
    if power < 0.0 {
      let norms = if spec.crowding {
        economy.crowd += 1.0;
        economy.crowd
      } else {
        1.0
      };
      economy.peak -= power * norms;
      economy.demand -= (if always_busy(spec) || world.has(entity, "Busy") { power } else { idle_power(power) }) * norms;
      powered += income;
    } else {
      economy.income += income;
    }
  }
  economy.produced += economy.stored;
  if powered != 0.0 {
    economy.income += powered * 1f64.min(economy.produced / economy.demand);
  }
  economy
}
