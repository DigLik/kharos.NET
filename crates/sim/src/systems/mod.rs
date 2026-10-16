//! Системы на Rust по именам из порядка тика `sim-ts` (createSim). Каждая объявляет, какие компоненты читает и пишет:
//! перед ней TS досылает в Rust именно их (docs/boundary.md, §4).

use crate::{Sim, Time};

mod turrets;

/// Система: имя в порядке тика, что читает (и пишет — только из прочитанного), и сама функция.
pub struct System {
  pub name: &'static str,
  pub reads: &'static [&'static str],
  pub run: fn(&mut Sim, Time),
}

/// Системы, которые уже на Rust.
pub const SYSTEMS: &[System] = &[System { name: "followCarriers", reads: &["Attached", "Position", "Turret", "Unit", "Building"], run: |sim, time| turrets::follow_carriers(&mut sim.world, time) }, System { name: "restTurrets", reads: &["Turret", "Armed", "Repair"], run: |sim, time| turrets::rest_turrets(&mut sim.world, time) }];

/// Что читает система; None — такой на Rust нет.
pub fn reads(name: &str) -> Option<&'static [&'static str]> {
  SYSTEMS.iter().find(|system| system.name == name).map(|system| system.reads)
}

/// Выполняет систему; false — такой на Rust нет.
pub fn run(sim: &mut Sim, name: &str, time: Time) -> bool {
  let Some(system) = SYSTEMS.iter().find(|system| system.name == name) else { return false };
  (system.run)(sim, time);
  sim.settle();
  true
}
