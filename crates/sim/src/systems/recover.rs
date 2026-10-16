//! Самовосстановление (`recover` в combat.ts): прочность понемногу возвращается сама — у кого есть regen, — рядом
//! с ремонтной станцией хозяина быстрее.

use crate::data::DATA;
use crate::math::js_hypot;
use crate::values::{number, patch, text};
use crate::{Sim, Time};

/// Во сколько раз быстрее восстанавливается своё рядом с ремонтной станцией при полной энергии.
pub const STATION_REGEN: f64 = 4.0;

struct Station {
  player: f64,
  x: f64,
  y: f64,
  radius: f64,
  share: f64,
}

/// Ремонтные станции, которые работают: готовые и с энергией (`repairStations`).
fn repair_stations(sim: &Sim) -> Vec<Station> {
  let world = &sim.world;
  let mut stations = Vec::new();
  for entity in world.query(&["Repair", "Building", "Position", "Owner"]) {
    if world.has(entity, "Site") {
      continue;
    }
    let Some(spec) = text(world, entity, "Building", "type").and_then(|kind| DATA.buildings.get(kind)) else { continue };
    // Выключенный потребитель и потребитель вне сети в долях энергии не значатся; здание без расхода работает всегда.
    let share = if spec.power.unwrap_or(0.0) < 0.0 { sim.power.get(&entity).copied().unwrap_or(0.0) } else { 1.0 };
    if share <= 0.0 {
      continue;
    }
    let field = |key, name| number(world, entity, key, name).unwrap_or(0.0);
    stations.push(Station { player: field("Owner", "player"), x: field("Position", "x") + spec.width / 2.0, y: field("Position", "y") + spec.height / 2.0, radius: field("Repair", "radius"), share });
  }
  stations
}

/// Раз в тик: прочность возвращается, кроме как у задетых в этот тик (`recover`).
pub fn recover(sim: &mut Sim, time: Time) {
  let stations = repair_stations(sim);
  let world = &mut sim.world;
  for entity in world.query(&["Health"]) {
    let field = |name| number(world, entity, "Health", name).unwrap_or(0.0);
    let (value, max, regen, hit) = (field("value"), field("max"), field("regen"), field("hit"));
    if regen <= 0.0 || hit == time.tick as f64 || value >= max {
      continue;
    }
    let mut boost: f64 = 1.0;
    if !stations.is_empty() {
      let position = number(world, entity, "Position", "x").zip(number(world, entity, "Position", "y"));
      let player = number(world, entity, "Owner", "player").unwrap_or(0.0);
      for station in &stations {
        let Some((x, y)) = position else { break };
        if station.player == player && js_hypot(x - station.x, y - station.y) <= station.radius {
          boost = boost.max(1.0 + (STATION_REGEN - 1.0) * station.share);
        }
      }
    }
    world.set(entity, "Health", patch([("value", max.min(value + regen * boost * time.step))]));
  }
}
