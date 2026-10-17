//! Накопители (income.ts, `storeEnergy`): до всех потребителей решают, сколько энергии отдают зоне в этот тик и сколько
//! берут из излишка.

use crate::data::DATA;
use crate::economy::economy_of_zone;
use crate::values::{js_number, number, text};
use crate::{Sim, Time};
use serde_json::Map;

pub fn store_energy(sim: &mut Sim, time: Time) {
  for entity in sim.world.query(&["Accumulator"]) {
    sim.world.set(entity, "Accumulator", Map::from_iter([("flow".to_owned(), js_number(0.0))]));
  }
  let grids: Vec<Vec<_>> = sim.zones.grids(&sim.world, &sim.occupancy, &sim.layers).iter().flat_map(|(_, zones)| zones.iter().map(|zone| zone.buildings.clone())).collect();
  let world = &mut sim.world;
  for buildings in grids {
    let cells: Vec<_> = buildings.iter().copied().filter(|&entity| world.has(entity, "Accumulator")).collect();
    if cells.is_empty() {
      continue;
    }
    let economy = economy_of_zone(world, &buildings, time.step, false);
    let mut balance = economy.produced - economy.demand;
    for entity in cells {
      let Some(spec) = text(world, entity, "Building", "type").and_then(|kind| DATA.buildings[kind].battery.as_ref()) else { continue };
      let charge = number(world, entity, "Accumulator", "charge").unwrap_or(0.0);
      let mut flow = 0.0;
      if balance < 0.0 && charge > 0.0 {
        flow = spec.rate.min(-balance).min(charge / time.step);
      } else if balance > 0.0 && charge < spec.capacity {
        flow = -spec.rate.min(balance).min((spec.capacity - charge) / time.step);
      }
      balance += flow;
      let charge = 0f64.max(spec.capacity.min(charge - flow * time.step));
      world.set(entity, "Accumulator", Map::from_iter([("flow".to_owned(), js_number(flow)), ("charge".to_owned(), js_number(charge))]));
    }
  }
}
