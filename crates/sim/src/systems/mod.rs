//! Системы на Rust по именам из порядка тика `sim-ts` (createSim). Каждая объявляет, какие компоненты читает и пишет:
//! перед ней TS досылает в Rust именно их (docs/boundary.md, §4).

use crate::Time;
use kharos_engine::World;

mod turrets;

/// Система: имя в порядке тика, что читает (и пишет — только из прочитанного), и сама функция.
pub struct System {
  pub name: &'static str,
  pub reads: &'static [&'static str],
  pub run: fn(&mut World, Time),
}

/// Системы, которые уже на Rust.
pub const SYSTEMS: &[System] = &[System { name: "followCarriers", reads: &["Attached", "Position", "Turret", "Unit", "Building"], run: turrets::follow_carriers }, System { name: "restTurrets", reads: &["Turret", "Armed", "Repair"], run: turrets::rest_turrets }];

/// Что читает система; None — такой на Rust нет.
pub fn reads(name: &str) -> Option<&'static [&'static str]> {
  SYSTEMS.iter().find(|system| system.name == name).map(|system| system.reads)
}

/// Выполняет систему; false — такой на Rust нет.
pub fn run(world: &mut World, name: &str, time: Time) -> bool {
  let Some(system) = SYSTEMS.iter().find(|system| system.name == name) else { return false };
  (system.run)(world, time);
  true
}
