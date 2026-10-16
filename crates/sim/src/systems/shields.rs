//! Щиты (shields.ts): купол восстанавливается на долю энергии своей зоны; без энергии гаснет.

use crate::data::DATA;
use crate::values::{number, patch, text};
use crate::{Sim, Time};

/// Раз в тик: щиты восстанавливаются, но не раньше rules.repairPause секунд после последнего погашенного попадания и
/// не пока купол пробит (`chargeShields`).
pub fn charge_shields(sim: &mut Sim, time: Time) {
  let pause = sim.rule("repairPause") / time.step;
  let world = &mut sim.world;
  let tick = time.tick as f64;
  for entity in world.query(&["Shield", "Building"]) {
    if world.has(entity, "Site") {
      continue;
    }
    let share = sim.power.get(&entity).copied().unwrap_or(0.0);
    if share <= 0.0 {
      world.set(entity, "Shield", patch([("charge", 0.0)]));
      continue;
    }
    let field = |name| number(world, entity, "Shield", name).unwrap_or(0.0);
    let (charge, hit, down) = (field("charge"), field("hit"), field("down"));
    if tick < down || (hit >= 0.0 && tick - hit < pause) {
      continue;
    }
    let Some(shield) = text(world, entity, "Building", "type").and_then(|kind| DATA.buildings.get(kind)).and_then(|spec| spec.shield.as_ref()) else { continue };
    if charge < shield.capacity {
      world.set(entity, "Shield", patch([("charge", shield.capacity.min(charge + shield.regen * share * time.step))]));
    }
  }
}
