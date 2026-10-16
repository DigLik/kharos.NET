//! Какими игроки помнят чужие здания (buildMemory.ts, docs/layers.md, С6). Здание, ушедшее в туман войны, остаётся для
//! игрока призраком, пока его место снова не окажется в обзоре. Здесь решается, кто что запомнил и забыл; сам снимок
//! здания (JSON его сохраняемых компонентов) снимает зеркало TS по событию `Remember` — на том же мире этого мига.

use crate::data::DATA;
use crate::occupancy::{Occupancy, CELL};
use crate::values::{number, text};
use crate::vision::Vision;
use kharos_engine::{Entity, Layers, World};
use std::collections::{BTreeMap, BTreeSet};

/// Раз во сколько тиков игроки сверяют увиденное: обзор и так пересчитывается не каждый тик.
const LEARN_TICKS: i64 = 4;

/// Где запомненное здание и какого оно вида: по ним видно, что место снова в обзоре.
#[derive(Clone, Debug, PartialEq)]
pub struct Ghost {
  pub x: f64,
  pub y: f64,
  pub kind: String,
}

#[derive(Clone, Debug, PartialEq)]
pub enum Event {
  /// Игрок запомнил здание таким, какое оно сейчас.
  Remember { player: u32, entity: Entity },
  /// Игрок забыл здание: увидел его снова или увидел, что его больше нет.
  Forget { player: u32, entity: Entity },
}

#[derive(Default)]
pub struct BuildMemory {
  pub known: BTreeMap<u32, BTreeMap<Entity, Ghost>>,
  /// Чужие здания, которые игрок видел при прошлой сверке: ушедшее из обзора запоминается.
  watched: BTreeMap<u32, BTreeSet<Entity>>,
  events: Vec<Event>,
}

/// Видит ли игрок хоть один тайл основания здания вида kind с левым верхним углом в (x, y).
fn in_sight(vision: &Vision, layers: &Layers, player: u32, x: f64, y: f64, kind: &str) -> bool {
  let Some(spec) = DATA.buildings.get(kind) else { return false };
  let (mut dy, mut seen) = (0.0, false);
  while dy < spec.height && !seen {
    let mut dx = 0.0;
    while dx < spec.width && !seen {
      seen = vision.sees(layers, player, x + dx, y + dy);
      dx += 1.0;
    }
    dy += 1.0;
  }
  seen
}

fn ghost_of(world: &World, entity: Entity) -> Ghost {
  let field = |name| number(world, entity, "Position", name).unwrap_or(0.0);
  Ghost { x: field("x"), y: field("y"), kind: text(world, entity, "Building", "type").unwrap_or_default().to_string() }
}

impl BuildMemory {
  /// Здание, которое игрок помнит, из сохранения.
  pub fn know(&mut self, player: u32, entity: Entity, ghost: Ghost) {
    self.known.entry(player).or_default().insert(entity, ghost);
  }

  /// Игрок начинает заново: что он помнил, больше ничего не значит (`forgetBuildings`).
  pub fn forget_player(&mut self, player: u32) {
    self.known.remove(&player);
    self.watched.remove(&player);
  }

  pub fn take_events(&mut self) -> Vec<Event> {
    std::mem::take(&mut self.events)
  }

  /// Система тика, после обзора (`updateBuildMemory`): ушедшее из обзора запоминается, что снова в обзоре — забывается,
  /// как и то, чего больше нет, а место в обзоре.
  pub fn update(&mut self, world: &World, vision: &Vision, layers: &Layers, occupancy: &Occupancy, tick: i64) {
    // Без тумана всё видно всегда: помнить нечего.
    if !vision.fog || tick % LEARN_TICKS != 0 {
      return;
    }
    for &player in vision.players() {
      let mut seen = BTreeSet::new();
      for cell in vision.seen(player, CELL) {
        for &entity in occupancy.in_cell(cell) {
          if seen.contains(&entity) || number(world, entity, "Owner", "player") == Some(player as f64) {
            continue;
          }
          let ghost = ghost_of(world, entity);
          if in_sight(vision, layers, player, ghost.x, ghost.y, &ghost.kind) {
            seen.insert(entity);
          }
        }
      }
      // Снесённое на глазах не запоминается.
      for &entity in self.watched.get(&player).into_iter().flatten() {
        if seen.contains(&entity) || !world.has(entity, "Building") {
          continue;
        }
        self.known.entry(player).or_default().insert(entity, ghost_of(world, entity));
        self.events.push(Event::Remember { player, entity });
      }
      if let Some(known) = self.known.get_mut(&player) {
        // Здания не двигаются: живое в обзоре — в seen, и место проверяется только у тех, которых больше нет.
        known.retain(|&entity, ghost| {
          let keep = !seen.contains(&entity) && (world.has(entity, "Building") || !in_sight(vision, layers, player, ghost.x, ghost.y, &ghost.kind));
          if !keep {
            self.events.push(Event::Forget { player, entity });
          }
          keep
        });
      }
      self.watched.insert(player, seen);
    }
  }
}
