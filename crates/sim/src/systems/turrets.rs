//! Турели на носителях (turrets.ts): встают на свои места и доворачиваются по корпусу.

use crate::data::DATA;
use crate::math::turn_toward;
use crate::values::{entity, number, patch, text};
use crate::Time;
use kharos_engine::{Entity, World};

/// Центр и направление носителя: у здания Position — левый верхний угол, у юнита — уже центр (`carrierPose`).
fn carrier_pose(world: &World, carrier: Entity) -> Option<(f64, f64, f64)> {
  let x = number(world, carrier, "Position", "x")?;
  let y = number(world, carrier, "Position", "y")?;
  if world.has(carrier, "Unit") {
    return Some((x, y, number(world, carrier, "Unit", "facing").unwrap_or(0.0)));
  }
  let spec = DATA.buildings.get(text(world, carrier, "Building", "type")?)?;
  Some((x + spec.width / 2.0, y + spec.height / 2.0, 0.0))
}

/// Раз в тик, после движения: турели встают на свои места на юнитах и зданиях, а турели погибших носителей исчезают
/// (`followCarriers`).
pub fn follow_carriers(world: &mut World, _time: Time) {
  let mut orphans = Vec::new();
  for turret in world.query(&["Attached", "Position", "Turret"]) {
    let x = number(world, turret, "Position", "x").unwrap_or(0.0);
    let y = number(world, turret, "Position", "y").unwrap_or(0.0);
    let angle = number(world, turret, "Turret", "angle").unwrap_or(0.0);
    world.set(turret, "Turret", patch([("prevX", x), ("prevY", y), ("prevAngle", angle)]));
    let pose = entity(world, turret, "Attached", "parent").and_then(|parent| carrier_pose(world, parent));
    let Some((cx, cy, facing)) = pose else {
      orphans.push(turret);
      continue;
    };
    let along = number(world, turret, "Attached", "along").unwrap_or(0.0);
    let across = number(world, turret, "Attached", "across").unwrap_or(0.0);
    // Как mountedAt: тот же порядок действий — тот же результат до последнего знака.
    let mounted_x = cx + along * facing.cos() - across * facing.sin();
    let mounted_y = cy + along * facing.sin() + across * facing.cos();
    world.set(turret, "Position", patch([("x", mounted_x), ("y", mounted_y)]));
  }
  // Состав мира меняется после обхода.
  for turret in orphans {
    world.destroy(turret);
  }
}

/// Раз в тик, после работ и боя: турель без цели и без работы поворачивается туда же, куда смотрит её носитель
/// (`restTurrets`).
pub fn rest_turrets(world: &mut World, time: Time) {
  for turret in world.query(&["Turret"]) {
    if entity(world, turret, "Armed", "target").is_some() || entity(world, turret, "Repair", "target").is_some() {
      continue;
    }
    let Some(spec) = text(world, turret, "Turret", "type").and_then(|kind| DATA.turrets.get(kind)) else { continue };
    let angle = number(world, turret, "Turret", "angle").unwrap_or(0.0);
    world.set(turret, "Turret", patch([("angle", turn_toward(angle, 0.0, spec.turn * time.step))]));
  }
}
