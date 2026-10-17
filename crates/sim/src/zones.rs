//! Сети труб и энергосети (zones.ts): связные группы готовых зданий и труб игрока, их круги, стройки и рёбра.
//! Выводятся из мира и пересчитываются, только когда появилось или пропало здание или стройка, — как кэш в TS.

use crate::data::DATA;
use crate::math::js_hypot;
use crate::occupancy::Occupancy;
use crate::values::{number, text};
use kharos_engine::{Entity, Layers, World};
use std::collections::HashMap;

/// На сколько тайлов от своего центра расширяет зону готовое здание без своего радиуса.
pub const EXPAND_RADIUS: f64 = 5.0;
/// Дальше скольких тайлов колодцы не связываются подземным отрезком.
const UNDERGROUND_REACH: i32 = 10;
/// Главное здание.
pub const CORE: &str = "command";

/// Расстояние от точки до основания (`linkGap`).
pub fn link_gap(cx: f64, cy: f64, x: f64, y: f64, width: f64, height: f64) -> f64 {
  js_hypot((x - cx).max(0.0).max(cx - x - width), (y - cy).max(0.0).max(cy - y - height))
}

/// Попадает ли точка хотя бы в один круг: x, y и радиус подряд (`inCircles`).
pub fn in_circles(circles: &[f64], x: f64, y: f64) -> bool {
  circles.chunks_exact(3).any(|circle| js_hypot(x - circle[0], y - circle[1]) <= circle[2])
}

/// Сеть труб или энергосеть (`Zone`).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Zone {
  pub circles: Vec<f64>,
  pub buildings: Vec<Entity>,
  pub sites: Vec<Entity>,
  pub edges: Vec<f64>,
}

/// Сети игроков по порядку появления игроков, у каждого — сначала с главным зданием.
pub type ByPlayer = Vec<(f64, Vec<Zone>)>;

#[derive(Default)]
pub struct Zones {
  /// Счётчики состава Building и Site, при которых посчитано.
  seen: Option<(u64, u64)>,
  zones: Option<ByPlayer>,
  grids: Option<ByPlayer>,
  /// Сеть сущности: игрок по порядку и сеть по порядку.
  index: HashMap<Entity, (usize, usize)>,
  grid_index: HashMap<Entity, (usize, usize)>,
  /// Растёт с каждым пересчётом энергосетей: по нему хозяйства знают, что сети перестроились.
  pub revision: u64,
}

fn building_type(world: &World, entity: Entity) -> &str {
  text(world, entity, "Building", "type").unwrap_or_default()
}

fn is_core(world: &World, entity: Option<&Entity>) -> bool {
  entity.is_some_and(|&entity| building_type(world, entity) == CORE)
}

fn pipe_of(world: &World, entity: Entity) -> Option<&'static str> {
  DATA.buildings.get(building_type(world, entity)).and_then(|spec| spec.pipe.as_deref())
}

/// Соседи здания по сторонам основания, каждый раз (`neighbours`).
pub fn neighbours(occupancy: &Occupancy, layers: &Layers, entity: Entity, x: f64, y: f64, kind: &str) -> Vec<Entity> {
  let spec = &DATA.buildings[kind];
  let mut found = Vec::new();
  let mut see = |x: f64, y: f64| {
    if let Some(other) = occupancy.at(layers, x.floor() as i32, y.floor() as i32) {
      if other != entity && !found.contains(&other) {
        found.push(other);
      }
    }
  };
  let mut tile_x = x;
  while tile_x < x + spec.width {
    see(tile_x, y - 1.0);
    see(tile_x, y + spec.height);
    tile_x += 1.0;
  }
  let mut tile_y = y;
  while tile_y < y + spec.height {
    see(x - 1.0, tile_y);
    see(x + spec.width, tile_y);
    tile_y += 1.0;
  }
  found
}

impl Zones {
  fn fresh(&mut self, world: &World) {
    let now = (world.changes("Building"), world.changes("Site"));
    if self.seen != Some(now) {
      self.seen = Some(now);
      self.zones = None;
      self.grids = None;
      self.index.clear();
      self.grid_index.clear();
    }
  }

  /// Сети труб всех игроков (`allZones`).
  pub fn all(&mut self, world: &World, occupancy: &Occupancy, layers: &Layers) -> &ByPlayer {
    self.fresh(world);
    if self.zones.is_none() {
      let zones = compute(world, occupancy, layers);
      for (player, (_, list)) in zones.iter().enumerate() {
        for (at, zone) in list.iter().enumerate() {
          for &entity in zone.buildings.iter().chain(&zone.sites) {
            self.index.insert(entity, (player, at));
          }
        }
      }
      self.zones = Some(zones);
    }
    self.zones.as_ref().unwrap()
  }

  /// Сеть, в которую входит здание, труба или подключённая стройка (`networkOf`).
  pub fn network_of(&mut self, world: &World, occupancy: &Occupancy, layers: &Layers, entity: Entity) -> Option<&Zone> {
    self.all(world, occupancy, layers);
    let &(player, at) = self.index.get(&entity)?;
    Some(&self.zones.as_ref().unwrap()[player].1[at])
  }

  /// Энергосети всех игроков (`allGrids`).
  pub fn grids(&mut self, world: &World, occupancy: &Occupancy, layers: &Layers) -> &ByPlayer {
    self.all(world, occupancy, layers);
    if self.grids.is_none() {
      let grids: ByPlayer = self.zones.as_ref().unwrap().iter().map(|(player, list)| (*player, merge_touching(world, list))).collect();
      for (player, (_, list)) in grids.iter().enumerate() {
        for (at, grid) in list.iter().enumerate() {
          for &entity in &grid.buildings {
            self.grid_index.insert(entity, (player, at));
          }
        }
      }
      self.grids = Some(grids);
      self.revision += 1;
    }
    self.grids.as_ref().unwrap()
  }

  /// Энергосеть здания или трубы (`gridOf`).
  pub fn grid_of(&mut self, world: &World, occupancy: &Occupancy, layers: &Layers, entity: Entity) -> Option<&Zone> {
    self.grids(world, occupancy, layers);
    let &(player, at) = self.grid_index.get(&entity)?;
    Some(&self.grids.as_ref().unwrap()[player].1[at])
  }

  /// Сети одного игрока (`zonesOf`).
  pub fn of_player(&mut self, world: &World, occupancy: &Occupancy, layers: &Layers, player: f64) -> &[Zone] {
    self.all(world, occupancy, layers).iter().find(|(other, _)| *other == player).map_or(&[], |(_, list)| list.as_slice())
  }

  /// Задевает ли основание чужую зону строительства (`inForeignZone`).
  #[allow(clippy::too_many_arguments)]
  pub fn in_foreign(&mut self, world: &World, occupancy: &Occupancy, layers: &Layers, player: f64, x: f64, y: f64, width: f64, height: f64) -> bool {
    for (other, zones) in self.all(world, occupancy, layers) {
      if *other == player {
        continue;
      }
      for Zone { circles, .. } in zones {
        if in_circles(circles, x + width / 2.0, y + height / 2.0) {
          return true;
        }
        if in_circles(circles, x, y) || in_circles(circles, x + width, y) {
          return true;
        }
        if in_circles(circles, x, y + height) || in_circles(circles, x + width, y + height) {
          return true;
        }
      }
    }
    false
  }
}

/// Корень множества с сокращением путей.
fn find(parent: &mut HashMap<Entity, Entity>, entity: Entity) -> Entity {
  let mut root = entity;
  while let Some(&next) = parent.get(&root) {
    root = next;
  }
  let mut entity = entity;
  while entity != root {
    let next = parent[&entity];
    parent.insert(entity, root);
    entity = next;
  }
  root
}

fn compute(world: &World, occupancy: &Occupancy, layers: &Layers) -> ByPlayer {
  // Готовые здания и трубы, которые входят в сети, по порядку: сущность — игрок.
  let mut members: Vec<(Entity, f64)> = Vec::new();
  let mut member: HashMap<Entity, f64> = HashMap::new();
  for entity in world.query(&["Building", "Owner"]) {
    if world.has(entity, "Site") || DATA.buildings[building_type(world, entity)].isolated {
      continue;
    }
    let player = number(world, entity, "Owner", "player").unwrap_or(0.0);
    members.push((entity, player));
    member.insert(entity, player);
  }
  let mut parent: HashMap<Entity, Entity> = HashMap::new();
  let mut edges: Vec<f64> = Vec::new();
  let mut link = |parent: &mut HashMap<Entity, Entity>, a: Entity, b: Entity, length: f64| {
    edges.extend([a as f64, b as f64, length]);
    let (one, two) = (find(parent, a), find(parent, b));
    if one != two {
      parent.insert(two, one);
    }
  };
  let position = |entity: Entity| (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
  let at = |x: f64, y: f64| occupancy.at(layers, x.floor() as i32, y.floor() as i32);
  for entity in world.query(&["Position", "Building"]) {
    let kind = pipe_of(world, entity);
    let Some(&player) = member.get(&entity) else { continue };
    let Some(kind) = kind else { continue };
    let (x, y) = position(entity);
    for (dx, dy) in [(1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0)] {
      let Some(other) = at(x + dx, y + dy) else { continue };
      if member.get(&other) != Some(&player) {
        continue;
      }
      // Две соседние трубы видят друг друга дважды: ребро — от меньшей.
      if pipe_of(world, other).is_some() && other < entity {
        continue;
      }
      link(&mut parent, entity, other, 1.0);
    }
    if kind != "well" {
      continue;
    }
    for (dx, dy) in [(1.0, 0.0), (0.0, 1.0)] {
      for step in 2..=UNDERGROUND_REACH {
        let Some(other) = at(x + dx * step as f64, y + dy * step as f64) else { continue };
        if member.get(&other) != Some(&player) || pipe_of(world, other) != Some("well") {
          continue;
        }
        link(&mut parent, entity, other, step as f64);
        break;
      }
    }
  }

  // Здания вплотную: стык — от меньшего к большему, только у зданий одного вида.
  for entity in world.query(&["Position", "Building"]) {
    let Some(&player) = member.get(&entity) else { continue };
    let kind = building_type(world, entity);
    if DATA.buildings[kind].pipe.is_some() {
      continue;
    }
    let (x, y) = position(entity);
    for other in neighbours(occupancy, layers, entity, x, y, kind) {
      if other < entity || member.get(&other) != Some(&player) || pipe_of(world, other).is_some() {
        continue;
      }
      if building_type(world, other) != kind {
        continue;
      }
      let (there_x, there_y) = position(other);
      let spec = &DATA.buildings[building_type(world, other)];
      let own = &DATA.buildings[kind];
      let length = (there_x + spec.width / 2.0 - x - own.width / 2.0).abs() + (there_y + spec.height / 2.0 - y - own.height / 2.0).abs();
      link(&mut parent, entity, other, 1f64.max(crate::math::js_round(length)));
    }
  }

  let mut by_root: HashMap<Entity, (usize, usize)> = HashMap::new();
  let mut result: ByPlayer = Vec::new();
  for &(entity, player) in &members {
    let root = find(&mut parent, entity);
    let (list, at) = match by_root.get(&root) {
      Some(&place) => place,
      None => {
        let list = match result.iter().position(|(other, _)| *other == player) {
          Some(list) => list,
          None => {
            result.push((player, Vec::new()));
            result.len() - 1
          }
        };
        result[list].1.push(Zone::default());
        let place = (list, result[list].1.len() - 1);
        by_root.insert(root, place);
        place
      }
    };
    let (x, y) = position(entity);
    let kind = building_type(world, entity);
    let spec = &DATA.buildings[kind];
    let zone = &mut result[list].1[at];
    zone.circles.extend([x + spec.width / 2.0, y + spec.height / 2.0, spec.zone.or(spec.expand).unwrap_or(EXPAND_RADIUS)]);
    if kind == CORE {
      zone.buildings.insert(0, entity);
    } else {
      zone.buildings.push(entity);
    }
  }
  for edge in edges.chunks_exact(3) {
    let (list, at) = by_root[&find(&mut parent, edge[0] as Entity)];
    result[list].1[at].edges.extend_from_slice(edge);
  }

  // Стройка входит в сеть первой трубы, подведённой к её основанию сбоку, или здания того же вида вплотную.
  for entity in world.query(&["Position", "Site", "Owner"]) {
    let demolish = world.get(entity, "Site").and_then(|site| site.get("demolish")).and_then(|value| value.as_bool()).unwrap_or(false);
    if demolish || !world.has(entity, "Inventory") {
      continue;
    }
    let kind = text(world, entity, "Site", "type").unwrap_or_default();
    let spec = &DATA.buildings[kind];
    let owner = number(world, entity, "Owner", "player").unwrap_or(0.0);
    let (x, y) = position(entity);
    let mut zone: Option<(usize, usize)> = None;
    let mut touch = |result: &mut ByPlayer, tile_x: f64, tile_y: f64| {
      let Some(other) = at(tile_x, tile_y) else { return };
      if member.get(&other) != Some(&owner) {
        return;
      }
      if pipe_of(world, other).is_none() && building_type(world, other) != kind {
        return;
      }
      let found = by_root[&find(&mut parent, other)];
      if zone.is_some_and(|zone| zone != found) {
        return;
      }
      if zone.is_none() {
        result[found.0].1[found.1].sites.push(entity);
      }
      zone = Some(found);
      result[found.0].1[found.1].edges.extend([entity as f64, other as f64, 1.0]);
    };
    let mut tile_x = x;
    while tile_x < x + spec.width {
      touch(&mut result, tile_x, y - 1.0);
      touch(&mut result, tile_x, y + spec.height);
      tile_x += 1.0;
    }
    let mut tile_y = y;
    while tile_y < y + spec.height {
      touch(&mut result, x - 1.0, tile_y);
      touch(&mut result, x + spec.width, tile_y);
      tile_y += 1.0;
    }
  }
  // Сети с главным зданием — первыми.
  for (_, zones) in result.iter_mut() {
    zones.sort_by_key(|zone| !is_core(world, zone.buildings.first()));
  }
  result
}

/// Сливает сети, задевающие друг друга, в энергосети (`mergeTouching`).
fn merge_touching(world: &World, zones: &[Zone]) -> Vec<Zone> {
  let mut parent: Vec<usize> = (0..zones.len()).collect();
  fn root(parent: &mut [usize], i: usize) -> usize {
    if parent[i] == i {
      return i;
    }
    let found = root(parent, parent[i]);
    parent[i] = found;
    found
  }
  let bases: Vec<Vec<f64>> = zones
    .iter()
    .map(|zone| {
      zone
        .buildings
        .iter()
        .flat_map(|&entity| {
          let spec = &DATA.buildings[building_type(world, entity)];
          [number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0), spec.width, spec.height]
        })
        .collect()
    })
    .collect();
  let touches = |base: &[f64], circles: &[f64]| base.chunks_exact(4).any(|base| circles.chunks_exact(3).any(|circle| link_gap(circle[0], circle[1], base[0], base[1], base[2], base[3]) <= circle[2]));
  for a in 0..zones.len() {
    for b in a + 1..zones.len() {
      if root(&mut parent, a) == root(&mut parent, b) {
        continue;
      }
      if touches(&bases[a], &zones[b].circles) || touches(&bases[b], &zones[a].circles) {
        let (to, from) = (root(&mut parent, a), root(&mut parent, b));
        parent[from] = to;
      }
    }
  }
  let mut merged: Vec<(usize, Zone)> = Vec::new();
  for (i, zone) in zones.iter().enumerate() {
    let top = root(&mut parent, i);
    match merged.iter_mut().find(|(other, _)| *other == top) {
      None => merged.push((top, Zone { circles: zone.circles.clone(), buildings: zone.buildings.clone(), sites: zone.sites.clone(), edges: Vec::new() })),
      Some((_, grid)) => {
        grid.circles.extend_from_slice(&zone.circles);
        if is_core(world, zone.buildings.first()) && !is_core(world, grid.buildings.first()) {
          grid.buildings.splice(0..0, zone.buildings.iter().copied());
        } else {
          grid.buildings.extend_from_slice(&zone.buildings);
        }
        grid.sites.extend_from_slice(&zone.sites);
      }
    }
  }
  merged.into_iter().map(|(_, grid)| grid).collect()
}
