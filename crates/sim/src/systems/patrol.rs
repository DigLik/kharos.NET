//! Патруль и возвращение на место (tactics.ts, `patrol`): вставшие без цели юниты идут к следующей точке обхода или
//! туда, откуда ушли в погоню.

use crate::math::js_hypot;
use crate::values::{number, patch};
use crate::walk::order_move;
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::{json, Map};

/// Раз во сколько тиков вставший юнит вспоминает о патруле и о месте, куда вернуться.
const RESUME_TICKS: u64 = 10;
/// Ближе скольких тайлов к своему месту юнит считается пришедшим: само место мог занять сосед.
const HOME_REACH: f64 = 1.5;

/// Турели носителя (`turretsOf`).
pub fn turrets_of(world: &World, entity: Entity) -> Vec<Entity> {
  let Some(turrets) = world.get(entity, "Carrier").and_then(|carrier| carrier.get("turrets")).and_then(|turrets| turrets.as_array()) else { return Vec::new() };
  turrets.iter().filter_map(|turret| turret.as_f64()).map(|turret| turret as Entity).collect()
}

/// Занят ли юнит боем: у него или у его турелей есть цель (`fighting`).
fn fighting(world: &World, entity: Entity) -> bool {
  std::iter::once(entity).chain(turrets_of(world, entity)).any(|gunner| number(world, gunner, "Armed", "target").unwrap_or(-1.0) >= 0.0)
}

/// Точки патруля юнита: x, y подряд.
fn stops_of(world: &World, entity: Entity) -> Vec<f64> {
  world.get(entity, "Tactics").and_then(|tactics| tactics.get("patrol")).and_then(|patrol| patrol.as_array()).map(|patrol| patrol.iter().filter_map(|value| value.as_f64()).collect()).unwrap_or_default()
}

fn set_leg(world: &mut World, entity: Entity, leg: f64) {
  world.set(entity, "Tactics", patch([("leg", leg)]));
}

fn set_home(world: &mut World, entity: Entity) {
  let mut fields = Map::new();
  fields.insert("away".to_owned(), json!(false));
  world.set(entity, "Tactics", fields);
}

/// Куда идёт юнит: в тайл (x, y); leg — к точке патруля.
struct Move {
  entity: Entity,
  x: f64,
  y: f64,
  leg: bool,
}

pub fn patrol(sim: &mut Sim, time: Time) {
  let world = &mut sim.world;
  let mut moves = Vec::new();
  for entity in world.query(&["Tactics", "Position"]) {
    let resupply = world.get(entity, "Tactics").and_then(|tactics| tactics.get("resupply")).and_then(|value| value.as_bool()).unwrap_or(false);
    if resupply || world.has(entity, "Path") || (time.tick + entity as u64) % RESUME_TICKS != 0 || fighting(world, entity) {
      continue;
    }
    let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    let stops = stops_of(world, entity);
    if !stops.is_empty() {
      let count = (stops.len() / 2) as f64;
      let mut leg = number(world, entity, "Tactics", "leg").unwrap_or(0.0);
      // Дошёл до точки — к следующей; встал, не дойдя (после боя), — снова к ней.
      if x.floor() == stops[leg as usize * 2] && y.floor() == stops[leg as usize * 2 + 1] {
        leg = (leg + 1.0) % count;
        set_leg(world, entity, leg);
      }
      moves.push(Move { entity, x: stops[leg as usize * 2], y: stops[leg as usize * 2 + 1], leg: true });
      continue;
    }
    let away = world.get(entity, "Tactics").and_then(|tactics| tactics.get("away")).and_then(|value| value.as_bool()).unwrap_or(false);
    if away {
      // Пришёл — или встал рядом, потому что место заняли. Не пришёл — идёт туда снова: встреченный по дороге враг
      // места назначения не отменяет.
      let (home_x, home_y) = (number(world, entity, "Tactics", "homeX").unwrap_or(0.0), number(world, entity, "Tactics", "homeY").unwrap_or(0.0));
      if js_hypot(x - home_x - 0.5, y - home_y - 0.5) <= HOME_REACH {
        set_home(world, entity);
      } else {
        moves.push(Move { entity, x: home_x, y: home_y, leg: false });
      }
    }
  }
  for Move { entity, x, y, leg } in moves {
    order_move(sim, entity, x as i32, y as i32, None, 0.0, 0.0, None);
    let world = &mut sim.world;
    if !world.has(entity, "Tactics") || world.has(entity, "Path") {
      continue;
    }
    // Ближе к точке патруля не подойти (она на здании, за обрывом): точка засчитана, юнит идёт к следующей. Иначе он
    // искал бы путь к ней каждые RESUME_TICKS.
    let stops = stops_of(world, entity);
    if leg && !stops.is_empty() {
      let leg = number(world, entity, "Tactics", "leg").unwrap_or(0.0);
      set_leg(world, entity, (leg + 1.0) % (stops.len() / 2) as f64);
    }
    // До места не добраться — остаётся где стоит.
    if !leg {
      set_home(world, entity);
    }
  }
}
