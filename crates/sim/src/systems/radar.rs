//! Разведка радара (radar.ts): раз в SCAN_EVERY секунд радар открывает вспышкой круг где-то в своём радиусе — там, где
//! ещё не разведано, а когда разведано всё — где придётся. При нехватке энергии вспышки реже.

use crate::data::{level_factor, DATA};
use crate::math::{js_cos, js_round, js_sin};
use crate::values::{number, patch, text};
use crate::{Sim, Time};
use kharos_engine::terrain::hash;
use serde_json::{json, Value};

/// Раз во сколько секунд радар вспыхивает при полной энергии.
pub const SCAN_EVERY: f64 = 10.0;
/// С какой доли нужной энергии радар видит дальше обычного здания.
pub const RADAR_POWER: f64 = 0.5;
/// Раз во сколько тиков радары смотрят, не пора ли.
const CHECK_TICKS: u64 = 10;
/// Сколько мест радар пробует, ища неразведанное.
const ATTEMPTS: u64 = 8;
const SALT: f64 = 40487.0;

/// Раз в тик: радары ведут счёт до вспышки и вспыхивают (`scan`).
pub fn scan(sim: &mut Sim, time: Time) {
  if time.tick % CHECK_TICKS != 0 {
    return;
  }
  let world = &mut sim.world;
  let mut due = Vec::new();
  for entity in world.query(&["Building", "Owner", "Position"]) {
    let Some(kind) = text(world, entity, "Building", "type") else { continue };
    let Some(spec) = DATA.buildings.get(kind) else { continue };
    let radius = spec.scan.unwrap_or(0.0) * level_factor(number(world, entity, "Building", "level").unwrap_or(1.0));
    if radius == 0.0 || world.has(entity, "Site") || world.has(entity, "Off") {
      continue;
    }
    if !world.has(entity, "Scan") {
      // Первая вспышка — через полный срок: радары, поставленные разом, всё равно расходятся — места у них разные.
      world.add(entity, "Scan", json!({ "x": 0, "y": 0, "tick": -1, "wait": crate::values::js_number(SCAN_EVERY / time.step), "dark": false }));
      continue;
    }
    // Энергии не хватает — счёт идёт медленнее.
    let share = sim.power.get(&entity).copied().unwrap_or(0.0);
    let wait = number(world, entity, "Scan", "wait").unwrap_or(0.0) - CHECK_TICKS as f64 * share;
    let mut fields = patch([("wait", wait)]);
    fields.insert("dark".to_owned(), Value::Bool(share < RADAR_POWER));
    world.set(entity, "Scan", fields);
    if wait > 0.0 {
      continue;
    }
    let player = number(world, entity, "Owner", "player").unwrap_or(0.0) as u32;
    let x = number(world, entity, "Position", "x").unwrap_or(0.0) + spec.width / 2.0;
    let y = number(world, entity, "Position", "y").unwrap_or(0.0) + spec.height / 2.0;
    due.push((entity, player, x, y, radius));
  }
  let bounds = sim.vision.bounds;
  for (entity, player, x, y, radius) in due {
    // Обзор хозяина — как при вопросе из TS: заводится, если его ещё нет.
    sim.vision.open(&sim.world, &mut sim.layers, player, time.tick as i64, false);
    let mut spot = None;
    for attempt in 0..ATTEMPTS {
      let n = (time.tick * ATTEMPTS + attempt) as f64;
      let turn = hash(entity as f64, n, SALT) * std::f64::consts::PI * 2.0;
      // Корень — чтобы места ложились по кругу ровно, а не кучей у радара.
      let reach = hash(entity as f64, n, SALT + 1.0).sqrt() * radius;
      let tx = x + js_cos(turn) * reach;
      let ty = y + js_sin(turn) * reach;
      if tx < bounds.left as f64 || tx >= bounds.right as f64 || ty < bounds.top as f64 || ty >= bounds.bottom as f64 {
        continue;
      }
      spot = Some((js_round(tx * 10.0) / 10.0, js_round(ty * 10.0) / 10.0));
      if !sim.vision.explored(&sim.layers, player, tx, ty) {
        break;
      }
    }
    let mut fields = vec![("wait", SCAN_EVERY / time.step)];
    if let Some((spot_x, spot_y)) = spot {
      fields.extend([("x", spot_x), ("y", spot_y), ("tick", time.tick as f64)]);
    }
    sim.world.set(entity, "Scan", fields.into_iter().map(|(name, value)| (name.to_owned(), crate::values::js_number(value))).collect());
  }
}
