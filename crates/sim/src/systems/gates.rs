//! Ворота (gates.ts): плиты поднимаются перед чужими наземными юнитами и опускаются за ними.

use crate::data::DATA;
use crate::math::js_hypot;
use crate::values::{number, text};
use crate::vision::allies_in;
use crate::Time;
use kharos_engine::{Entity, World};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};

/// Ближе скольких тайлов чужой поднимает ворота и дальше скольких они опускаются: запас не даёт им хлопать.
pub const GATE_CLOSE: f64 = 6.0;
pub const GATE_OPEN: f64 = 7.5;
/// Раз во сколько тиков ворота смотрят по сторонам.
const CHECK_TICKS: u64 = 4;

struct Section {
  entity: Entity,
  player: f64,
  /// Середина тайла.
  x: f64,
  y: f64,
  /// Ближайший чужой.
  foe: f64,
  /// В проёме кто-то стоит.
  held: bool,
}

/// Раз в тик: ворота закрываются перед чужими и открываются за ними (`gates`).
pub fn gates(world: &mut World, time: Time) {
  if time.tick % CHECK_TICKS != 0 {
    return;
  }
  let mut list = Vec::new();
  for entity in world.query(&["Building", "Owner", "Position"]) {
    let gate = text(world, entity, "Building", "type").and_then(|kind| DATA.buildings.get(kind)).is_some_and(|spec| spec.gate);
    if !gate || world.has(entity, "Site") {
      continue;
    }
    if !world.has(entity, "Gate") {
      world.add(entity, "Gate", json!({ "open": true }));
    }
    let field = |key, name| number(world, entity, key, name).unwrap_or(0.0);
    list.push(Section { entity, player: field("Owner", "player"), x: field("Position", "x") + 0.5, y: field("Position", "y") + 0.5, foe: f64::INFINITY, held: false });
  }
  if list.is_empty() {
    return;
  }
  // Ворота разложены по клеткам со стороной в порог открытия: юнит смотрит только свою клетку и соседние.
  let cell_of = |value: f64| (value / GATE_OPEN).floor() as i32;
  let mut cells: HashMap<(i32, i32), Vec<usize>> = HashMap::new();
  for (at, gate) in list.iter().enumerate() {
    cells.entry((cell_of(gate.x), cell_of(gate.y))).or_default().push(at);
  }
  let mut friends: HashMap<u64, Vec<f64>> = HashMap::new();
  for unit in world.query(&["Unit", "Owner", "Position"]) {
    if text(world, unit, "Unit", "type").and_then(|kind| DATA.units.get(kind)).is_some_and(|spec| spec.kind == "air") {
      continue;
    }
    let player = number(world, unit, "Owner", "player").unwrap_or(0.0);
    let (x, y) = (number(world, unit, "Position", "x").unwrap_or(0.0), number(world, unit, "Position", "y").unwrap_or(0.0));
    let (cx, cy) = (cell_of(x), cell_of(y));
    for dy in -1..=1 {
      for dx in -1..=1 {
        for &at in cells.get(&(cx + dx, cy + dy)).map_or(&[][..], Vec::as_slice) {
          let gate = &mut list[at];
          let distance = js_hypot(x - gate.x, y - gate.y);
          // В проёме — тот, чей центр в тайле ворот.
          if (x - gate.x).abs() < 0.5 && (y - gate.y).abs() < 0.5 {
            gate.held = true;
          }
          // Чужой — не хозяин, не ничей и не союзник хозяина.
          if player != gate.player && player != 0.0 && distance < gate.foe {
            let allies = friends.entry(gate.player as u64).or_insert_with(|| allies_in(world, gate.player as u32));
            if !allies.contains(&player) {
              gate.foe = distance;
            }
          }
        }
      }
    }
  }
  // Секции вплотную — одни ворота: чужой у одной поднимает все.
  let by_tile: HashMap<(i32, i32), usize> = list.iter().enumerate().map(|(at, gate)| ((gate.x.floor() as i32, gate.y.floor() as i32), at)).collect();
  let mut grouped = HashSet::new();
  for first in 0..list.len() {
    if !grouped.insert(first) {
      continue;
    }
    let mut group = vec![first];
    let mut at = 0;
    while at < group.len() {
      let (x, y, player) = (list[group[at]].x.floor() as i32, list[group[at]].y.floor() as i32, list[group[at]].player);
      for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
        let Some(&next) = by_tile.get(&(x + dx, y + dy)) else { continue };
        if list[next].player != player || grouped.contains(&next) {
          continue;
        }
        grouped.insert(next);
        group.push(next);
      }
      at += 1;
    }
    let foe = group.iter().map(|&at| list[at].foe).fold(f64::INFINITY, f64::min);
    for &at in &group {
      let gate = &list[at];
      let Some(open) = world.get(gate.entity, "Gate").and_then(|state| state.get("open")).and_then(Value::as_bool) else { continue };
      let next = if open && foe < GATE_CLOSE && !gate.held {
        false
      } else if !open && foe >= GATE_OPEN {
        true
      } else {
        continue;
      };
      world.set(gate.entity, "Gate", Map::from_iter([("open".to_owned(), Value::Bool(next))]));
    }
  }
}
