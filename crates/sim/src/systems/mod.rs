//! Системы на Rust по именам из порядка тика `sim-ts` (createSim). Каждая объявляет, какие компоненты читает и пишет:
//! перед ней TS досылает в Rust именно их (docs/boundary.md, §4).

use crate::recall::Source;
use crate::{Sim, Time};

mod movement;
mod turrets;

/// Система: имя в порядке тика, что читает (и пишет — только из прочитанного), и сама функция.
pub struct System {
  pub name: &'static str,
  pub reads: &'static [&'static str],
  pub run: fn(&mut Sim, Time),
}

/// Системы, которые на Rust: только движковые — пути, ход, носители, следы, обзор и память (docs/content.md, §5).
/// Механики — контент, их считает TS, пока их не перепишут модом `base`.
pub const SYSTEMS: &[System] = &[System { name: "planPaths", reads: &["Path", "Position", "Unit", "Converting", "Building", "Gate", "Site"], run: movement::plan_paths }, System { name: "moveUnits", reads: &["Position", "Unit", "Path", "Converting", "Owner", "Player", "Building", "Gate", "Site"], run: movement::move_units }, System { name: "followCarriers", reads: &["Attached", "Position", "Turret", "Unit", "Building"], run: |sim, time| turrets::follow_carriers(&mut sim.world, time) }, System { name: "traces", reads: &["Unit", "Position", "Owner"], run: |sim, time| sim.traces.update(&sim.world, &mut sim.layers, time.tick, time.step) }, System { name: "vision", reads: &["Unit", "Owner", "Position", "Attached", "Building", "Site", "Off", "Scan", "Glare", "Player"], run: |sim, time| sim.vision.update(&sim.world, &mut sim.layers, time.tick as i64) }, System { name: "updateLandMemory", reads: &[], run: |sim, time| sim.land_memory.update(&mut Source { land: &mut sim.land, layers: &sim.layers, world: &sim.world, vision: &sim.vision }, time.tick as i64) }, System { name: "updatePaveMemory", reads: &["Player"], run: |sim, time| sim.pave_memory.update(&mut Source { land: &mut sim.land, layers: &sim.layers, world: &sim.world, vision: &sim.vision }, time.tick as i64) }, System { name: "updateBuildMemory", reads: &["Owner", "Position", "Building"], run: |sim, time| sim.build_memory.update(&sim.world, &sim.vision, &sim.layers, &sim.occupancy, time.tick as i64) }];

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
