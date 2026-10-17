//! Разворачивание MCV в главное здание и сворачивание обратно (conversion.ts): `deployOrders` и `convert`.

use crate::common::{is_own, on_turn, owner_of, reward};
use crate::components;
use crate::data::DATA;
use crate::math::js_round;
use crate::placement::{can_place, clear_drops, clear_ground};
use crate::spawning::{place_building, spawn_unit};
use crate::values::{js_number, number, text};
use crate::walk::order_move;
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::{Map, Value};
use std::collections::HashSet;

/// Главное здание.
const CORE: &str = "command";
/// Сколько секунд MCV разворачивается (`DEPLOY_SECONDS`).
const DEPLOY_SECONDS: f64 = 3.0;
/// Раз во сколько тиков недоехавший MCV пробует снова; с каждым промахом — вдвое реже.
const DEPLOY_RETRY_TICKS: u64 = 20;
/// После скольких промахов подряд MCV бросает поездку.
const DEPLOY_MISSES: f64 = 5.0;
/// Раз во сколько тиков разворачивающийся MCV просит своих уйти из-под будущего здания.
const EVICT_TICKS: u64 = 20;

fn core_size() -> (f64, f64) {
  let spec = &DATA.buildings[CORE];
  (spec.width, spec.height)
}

fn position(world: &World, entity: Entity) -> (f64, f64) {
  (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0))
}

/// Где встанет главное здание, если MCV развернётся на месте (`siteUnder`).
fn site_under(world: &World, entity: Entity) -> (f64, f64) {
  let (x, y) = position(world, entity);
  let (width, height) = core_size();
  (x.floor() - (width / 2.0).floor(), y.floor() - (height / 2.0).floor())
}

/// Куда MCV послан разворачиваться, а не послан — под ним (`deploySite`).
fn deploy_site(world: &World, entity: Entity) -> (f64, f64) {
  match world.get(entity, "Deploys") {
    Some(_) => (number(world, entity, "Deploys", "x").unwrap_or(0.0), number(world, entity, "Deploys", "y").unwrap_or(0.0)),
    None => site_under(world, entity),
  }
}

/// Стоит ли MCV на основании будущего здания (`standsOn`).
fn stands_on(world: &World, entity: Entity, x: f64, y: f64) -> bool {
  let (px, py) = position(world, entity);
  let (width, height) = core_size();
  px >= x && px < x + width && py >= y && py < y + height
}

/// Середина основания главного здания: туда едет MCV (`middleOf`).
fn middle_of(x: f64, y: f64) -> (i32, i32) {
  let (width, height) = core_size();
  ((x + (width / 2.0).floor()) as i32, (y + (height / 2.0).floor()) as i32)
}

/// Можно ли послать этот MCV разворачиваться (`canSendDeploy`).
fn can_send_deploy(world: &World, player: f64, entity: Entity) -> bool {
  text(world, entity, "Unit", "type") == Some("mcv") && is_own(world, player, entity) && !world.has(entity, "Converting")
}

/// Может ли MCV развернуться на этом месте (`canDeployAt`).
fn can_deploy_at(sim: &mut Sim, player: f64, entity: Entity, x: f64, y: f64) -> bool {
  if !can_send_deploy(&sim.world, player, entity) || !can_place(sim, CORE, x, y, 0.0) {
    return false;
  }
  let (width, height) = core_size();
  !sim.zones.in_foreign(&sim.world, &sim.occupancy, &sim.layers, player, x, y, width, height)
}

/// Начинает превращение на seconds (`startConverting`).
fn start_converting(sim: &mut Sim, entity: Entity, seconds: f64, step: f64) {
  let ticks = js_round(seconds / step);
  sim.world.remove(entity, "Path");
  components::add(&mut sim.world, entity, "Converting", [("left", js_number(ticks)), ("total", js_number(ticks))]);
}

pub fn deploy_orders(sim: &mut Sim, time: Time) {
  let world = &sim.world;
  let mut arrived = Vec::new();
  let mut late = Vec::new();
  for entity in world.query(&["Deploys"]) {
    if world.has(entity, "Converting") || world.has(entity, "Path") {
      continue;
    }
    let (x, y) = deploy_site(world, entity);
    let misses = number(world, entity, "Deploys", "misses").unwrap_or(0.0);
    if stands_on(world, entity, x, y) {
      arrived.push(entity);
    } else if on_turn(time.tick, entity, DEPLOY_RETRY_TICKS << misses as u32) {
      late.push(entity);
    }
  }
  for entity in arrived {
    let (x, y) = deploy_site(&sim.world, entity);
    let player = owner_of(&sim.world, entity);
    if can_deploy_at(sim, player, entity, x, y) {
      start_converting(sim, entity, DEPLOY_SECONDS, time.step);
    } else {
      sim.world.remove(entity, "Deploys");
    }
  }
  for entity in late {
    let misses = number(&sim.world, entity, "Deploys", "misses").unwrap_or(0.0) + 1.0;
    sim.world.set(entity, "Deploys", Map::from_iter([("misses".to_owned(), js_number(misses))]));
    if misses > DEPLOY_MISSES {
      sim.world.remove(entity, "Deploys");
      continue;
    }
    let (x, y) = deploy_site(&sim.world, entity);
    let (to_x, to_y) = middle_of(x, y);
    order_move(sim, entity, to_x, to_y, None, 0.0, 0.0, None);
  }
}

/// Освобождает место под главное здание и говорит, свободно ли оно (`clearDeploySite`).
fn clear_deploy_site(sim: &mut Sim, mcv: Entity, evict: bool) -> bool {
  let (x, y) = deploy_site(&sim.world, mcv);
  let (width, height) = core_size();
  let player = owner_of(&sim.world, mcv);
  clear_ground(sim, player, x, y, width, height, evict, Some(mcv))
}

pub fn convert(sim: &mut Sim, time: Time) {
  let mut ready = Vec::new();
  let mut deploying = Vec::new();
  for entity in sim.world.query(&["Converting"]) {
    let left = 0f64.max(number(&sim.world, entity, "Converting", "left").unwrap_or(0.0) - 1.0);
    sim.world.set(entity, "Converting", Map::from_iter([("left".to_owned(), js_number(left))]));
    if sim.world.has(entity, "Unit") {
      deploying.push(entity);
    }
    if left <= 0.0 {
      ready.push(entity);
    }
  }
  // Выгонять пробуют не каждый тик: поиск пути недёшев.
  let mut blocked = HashSet::new();
  for entity in deploying {
    if !clear_deploy_site(sim, entity, on_turn(time.tick, entity, EVICT_TICKS)) {
      blocked.insert(entity);
    }
  }
  let (width, height) = core_size();
  for entity in ready.into_iter().filter(|entity| !blocked.contains(entity)) {
    sim.world.remove(entity, "Converting");
    let player = owner_of(&sim.world, entity);
    // Очередь производства и прочность переезжают вместе с игроком.
    let production = sim.world.get(entity, "Producer").and_then(Value::as_object).cloned();
    let health = number(&sim.world, entity, "Health", "value").unwrap_or(1.0);
    let health = Map::from_iter([("value".to_owned(), js_number(health))]);
    if sim.world.has(entity, "Unit") {
      let (x, y) = deploy_site(&sim.world, entity);
      // За время разворачивания место могли занять. Тогда MCV остаётся машиной.
      if !can_place(sim, CORE, x, y, 0.0) {
        sim.world.remove(entity, "Deploys");
        continue;
      }
      sim.world.destroy(entity);
      clear_drops(&mut sim.world, x, y, width, height);
      let core = place_building(&mut sim.world, CORE, x, y, player, 1.0);
      if let Some(production) = production {
        sim.world.set(core, "Producer", production);
      }
      sim.world.set(core, "Health", health);
      reward(&mut sim.world, player, "deploy");
    } else {
      let (x, y) = position(&sim.world, entity);
      sim.world.destroy(entity);
      let mcv = spawn_unit(&mut sim.world, "mcv", player, x + (width / 2.0).floor(), y + (height / 2.0).floor());
      if let Some(production) = production {
        sim.world.set(mcv, "Producer", production);
      }
      sim.world.set(mcv, "Health", health);
    }
  }
}
