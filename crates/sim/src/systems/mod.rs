//! Системы на Rust по именам из порядка тика `sim-ts` (createSim). Каждая объявляет, какие компоненты читает и пишет:
//! перед ней TS досылает в Rust именно их (docs/boundary.md, §4).

use crate::recall::Source;
use crate::{Sim, Time};

mod assembly;
mod busy;
mod combat;
mod conversion;
pub mod earn;
mod energy;
mod ferry;
mod gates;
mod movement;
mod patrol;
pub mod production;
mod radar;
mod recover;
mod refining;
mod shields;
mod reactors;
pub mod research;
mod trade;
mod turrets;

/// Система: имя в порядке тика, что читает (и пишет — только из прочитанного), и сама функция.
pub struct System {
  pub name: &'static str,
  pub reads: &'static [&'static str],
  pub run: fn(&mut Sim, Time),
}

/// Системы, которые уже на Rust.
pub const SYSTEMS: &[System] = &[System { name: "planPaths", reads: &["Path", "Position", "Unit", "Converting", "Building", "Gate", "Site"], run: movement::plan_paths }, System { name: "moveUnits", reads: &["Position", "Unit", "Path", "Converting", "Owner", "Player", "Building", "Gate", "Site"], run: movement::move_units }, System { name: "refine", reads: &["Building", "Inventory", "Site"], run: refining::refine }, System { name: "assemble", reads: &["Assembly", "Inventory", "Site"], run: assembly::assemble }, System { name: "recover", reads: &["Health", "Position", "Owner", "Repair", "Building", "Site"], run: recover::recover }, System { name: "research", reads: &["Building", "Owner", "Site", "Off", "Player", "Inventory", "Research", "Drop", "Trade", "Assembly", "Producer", "Position", "Wanted"], run: research::research }, System { name: "scan", reads: &["Building", "Owner", "Position", "Site", "Off", "Scan", "Unit", "Attached", "Glare", "Player"], run: radar::scan }, System { name: "chargeShields", reads: &["Shield", "Building", "Site"], run: shields::charge_shields }, System { name: "burnFuel", reads: &["Reactor", "Building", "Inventory", "Off"], run: |sim, time| reactors::burn_fuel(&mut sim.world, time) }, System { name: "gates", reads: &["Building", "Owner", "Position", "Site", "Gate", "Unit", "Player"], run: |sim, time| gates::gates(&mut sim.world, time) }, System { name: "markBusy", reads: &["Building", "Site", "Off", "Assembly", "Inventory", "Producer", "Converting", "Trade", "Research", "Repair", "Silo", "Interceptor", "Transfer", "Shield", "Armed", "Carrier", "Busy"], run: busy::mark_busy }, System { name: "storeEnergy", reads: &["Accumulator", "Building", "Owner", "Position", "Site", "Inventory", "Health", "Busy", "Off", "Assembly", "Reactor"], run: energy::store_energy }, System { name: "deployOrders", reads: &["Deploys", "Converting", "Path", "Position", "Unit", "Owner", "Building", "Site", "Pave", "Gate", "Player", "Inventory"], run: conversion::deploy_orders }, System { name: "convert", reads: &["Converting", "Unit", "Deploys", "Position", "Owner", "Path", "Producer", "Health", "Building", "Site", "Pave", "Gate", "Player", "Inventory", "Drop"], run: conversion::convert }, System { name: "produceUnits", reads: &["Producer", "Building", "Converting", "Site", "Unit", "Inventory", "Trade", "Position", "Owner", "Player", "Path", "Gate", "Pave"], run: production::produce_units }, System { name: "earn", reads: &["Player", "Building", "Health", "Reactor", "Off", "Inventory", "Silo", "Position"], run: earn::earn }, System { name: "trade", reads: &["Trade", "Owner", "Building", "Inventory", "Player"], run: trade::trade }, System { name: "ferry", reads: &["Transfer", "Player", "Building", "Inventory", "Owner", "Position", "Site", "Drop"], run: ferry::ferry }, System { name: "fight", reads: &["Position", "Unit", "Owner", "Health", "Building", "Armed", "Turret", "Attached", "Carrier", "Shot", "Blast", "Burst", "Doomed", "Tactics", "Path", "Converting", "Site", "Off", "Inventory", "Magazine", "Hauler", "Reactor", "Silo", "Shield", "Player", "Gate", "Pave", "Drop"], run: combat::fight }, System { name: "patrol", reads: &["Tactics", "Position", "Path", "Armed", "Carrier", "Unit", "Converting", "Owner", "Player", "Building", "Gate", "Site"], run: patrol::patrol }, System { name: "followCarriers", reads: &["Attached", "Position", "Turret", "Unit", "Building"], run: |sim, time| turrets::follow_carriers(&mut sim.world, time) }, System { name: "restTurrets", reads: &["Turret", "Armed", "Repair"], run: |sim, time| turrets::rest_turrets(&mut sim.world, time) }, System { name: "traces", reads: &["Unit", "Position", "Owner"], run: |sim, time| sim.traces.update(&sim.world, &mut sim.layers, time.tick, time.step) }, System { name: "vision", reads: &["Unit", "Owner", "Position", "Attached", "Building", "Site", "Off", "Scan", "Glare", "Player"], run: |sim, time| sim.vision.update(&sim.world, &mut sim.layers, time.tick as i64) }, System { name: "updateLandMemory", reads: &[], run: |sim, time| sim.land_memory.update(&mut Source { land: &mut sim.land, layers: &sim.layers, world: &sim.world, vision: &sim.vision }, time.tick as i64) }, System { name: "updatePaveMemory", reads: &["Player"], run: |sim, time| sim.pave_memory.update(&mut Source { land: &mut sim.land, layers: &sim.layers, world: &sim.world, vision: &sim.vision }, time.tick as i64) }, System { name: "updateBuildMemory", reads: &["Owner", "Position", "Building"], run: |sim, time| sim.build_memory.update(&sim.world, &sim.vision, &sim.layers, &sim.occupancy, time.tick as i64) }];

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
