//! Следы на земле (traces.ts, docs/layers.md, С7): колея и шаги, гарь, прожоги лучей, остовы — предметы слоя `traces`
//! по ячейкам 16×16. Живут свой срок и не сохраняются. Колею оставляет система `traces` по ходу наземных юнитов,
//! остальное — системы боя (`add`).

use crate::data::DATA;
use crate::math::{angle_between, js_hypot};
use crate::values::{number, text};
use kharos_engine::{Entity, Item, LayerId, Layers, Owner, World};
use std::collections::HashMap;

/// Виды следов по порядку — как TRACE_LIFE в traces.ts.
pub const TRACK: u8 = 0;
pub const SCAR: u8 = 1;
pub const BURN: u8 = 2;
pub const WRECK: u8 = 3;

/// Сколько секунд живёт след каждого вида.
const LIFE: [f64; 4] = [90.0, 240.0, 240.0, 600.0];
/// Через сколько тайлов пути юнит оставляет новую точку следа и на сколько радиан должен повернуть, чтобы оставить её раньше.
const TRACK_STEP: f64 = 0.5;
const TRACK_TURN: f64 = 0.35;
/// Как часто убираются истёкшие следы, в секундах.
const EXPIRE_EVERY: f64 = 1.0;
/// Сколько живых следов одного вида держит тайл; у остовов предела нет.
const DENSITY: [Option<u32>; 4] = [Some(2), Some(2), Some(2), None];

/// Поля предмета по порядку layers.json.
pub const ID: usize = 0;
pub const TICK: usize = 1;
pub const KIND: usize = 2;
pub const ENTITY: usize = 3;
pub const TYPE: usize = 4;
pub const FACING: usize = 5;
pub const PLAYER: usize = 6;
pub const SIZE: usize = 7;

/// Новый след: вид, место и поля вида (ENTITY, TYPE, FACING, PLAYER, SIZE), остальные — нули.
#[derive(Clone, Debug, PartialEq)]
pub struct NewTrace {
  pub kind: u8,
  pub x: f64,
  pub y: f64,
  pub entity: Entity,
  pub unit: u16,
  pub facing: f64,
  pub player: u16,
  pub size: f64,
}

pub struct Traces {
  layer: LayerId,
  /// Где каждый наземный юнит оставил последнюю точку.
  walkers: HashMap<Entity, (f64, f64, f64)>,
  /// Живые следы по тайлу и виду, см. DENSITY.
  density: HashMap<(i32, i32, u8), u32>,
  next: u32,
  /// Оставленные с конца прошлого тика: в конце этого они станут fresh.
  pending: Vec<Item>,
  fresh: Vec<Item>,
  expired: Vec<u32>,
  since_expire: f64,
}

/// Номер вида юнита — место в units.json.
pub fn unit_index(kind: &str) -> u16 {
  DATA.unit_types.iter().position(|other| other == kind).unwrap_or(0) as u16
}

fn density_key(kind: u8, x: f64, y: f64) -> (i32, i32, u8) {
  (x.floor() as i32, y.floor() as i32, kind)
}

impl Traces {
  pub fn new(layers: &Layers) -> Self {
    let layer = layers.id("traces").expect("нет слоя traces");
    Self { layer, walkers: HashMap::new(), density: HashMap::new(), next: 1, pending: Vec::new(), fresh: Vec::new(), expired: Vec::new(), since_expire: 0.0 }
  }

  /// Оставляет след в тике tick и отдаёт его; на полном тайле — нет.
  pub fn add(&mut self, layers: &mut Layers, trace: &NewTrace, tick: u64) -> Option<Item> {
    if let Some(limit) = DENSITY[trace.kind as usize] {
      let count = self.density.entry(density_key(trace.kind, trace.x, trace.y)).or_insert(0);
      if *count >= limit {
        return None;
      }
      *count += 1;
    }
    let item = Item { x: trace.x, y: trace.y, fields: vec![self.next as f64, tick as f64, trace.kind as f64, trace.entity as f64, trace.unit as f64, trace.facing, trace.player as f64, trace.size] };
    self.next += 1;
    layers.push(self.layer, Owner::World, &item);
    self.pending.push(item.clone());
    Some(item)
  }

  /// Следы, оставленные с конца прошлого тика: в конце этого они станут fresh.
  pub fn pending(&self) -> &[Item] {
    &self.pending
  }

  /// Следы, оставленные в последнем тике.
  pub fn fresh(&self) -> &[Item] {
    &self.fresh
  }

  /// Номера следов, убранных в последнем тике.
  pub fn expired(&self) -> &[u32] {
    &self.expired
  }

  /// Система `traces`, конец тика: точки пути наземных юнитов, истёкшие следы.
  pub fn update(&mut self, world: &World, layers: &mut Layers, tick: u64, step: f64) {
    let mut seen = Vec::new();
    for entity in world.query(&["Unit", "Position", "Owner"]) {
      let kind = text(world, entity, "Unit", "type").unwrap_or_default();
      if DATA.units.get(kind).is_some_and(|spec| spec.kind == "air") {
        continue;
      }
      seen.push(entity);
      let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
      let facing = number(world, entity, "Unit", "facing").unwrap_or(0.0);
      let last = self.walkers.get(&entity).copied();
      let moved = last.map_or(f64::INFINITY, |(last_x, last_y, _)| js_hypot(x - last_x, y - last_y));
      if let Some((_, _, last_facing)) = last {
        if moved < TRACK_STEP && angle_between(facing, last_facing).abs() < TRACK_TURN {
          continue;
        }
        if moved < 1e-3 {
          continue;
        }
      }
      self.walkers.insert(entity, (x, y, facing));
      // Первая точка — тоже точка: от неё колея и потянется.
      let player = number(world, entity, "Owner", "player").unwrap_or(0.0) as u16;
      self.add(layers, &NewTrace { kind: TRACK, x, y, entity, unit: unit_index(kind), facing, player, size: 0.0 }, tick);
    }
    let seen: std::collections::HashSet<Entity> = seen.into_iter().collect();
    self.walkers.retain(|entity, _| seen.contains(entity));

    self.fresh = std::mem::take(&mut self.pending);
    // Истёкшие убираются раз в EXPIRE_EVERY: сроки у видов разные, и просматривать приходится все.
    self.expired.clear();
    self.since_expire += step;
    if self.since_expire < EXPIRE_EVERY {
      return;
    }
    self.since_expire = 0.0;
    let now = tick as f64;
    let (density, expired) = (&mut self.density, &mut self.expired);
    for cell in layers.cells(self.layer, Owner::World) {
      layers.retain(self.layer, Owner::World, cell, |item| {
        let kind = item.fields[KIND] as u8;
        if (now - item.fields[TICK]) * step < LIFE[kind as usize] {
          return true;
        }
        expired.push(item.fields[ID] as u32);
        if DENSITY[kind as usize].is_some() {
          let key = density_key(kind, item.x, item.y);
          match density.get_mut(&key) {
            Some(count) if *count > 1 => *count -= 1,
            _ => {
              density.remove(&key);
            }
          }
        }
        false
      });
    }
    // Как в TS: убранные — по возрастанию номеров.
    expired.sort_unstable();
  }
}
