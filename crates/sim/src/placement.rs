//! Место под здание (buildings.ts, units.ts): можно ли его поставить, кто стоит на основании и как их оттуда выгнать,
//! дропы под ним.

use crate::data::DATA;
use crate::common::is_own;
use crate::values::{number, text};
use crate::walk::{flies, order_move, standing_units, Ground};
use crate::Sim;
use kharos_engine::terrain::{FOOT_BIT, MOUNTAIN, ROCK, SAND};
use kharos_engine::{Entity, World};
use std::collections::HashSet;

/// На сколько тайлов вокруг основания стоящие юниты не мешают выселенному выйти (`EVICT_MARGIN`).
const EVICT_MARGIN: f64 = 2.0;

/// Стенка обрыва у подножия: строить на ней нельзя (`isCliffFoot`).
pub fn is_cliff_foot(sim: &mut Sim, x: i32, y: i32) -> bool {
  let [terrain, _, _, relief] = sim.land.tile(x, y);
  terrain != MOUNTAIN && relief & FOOT_BIT != 0
}

/// Работа над покрытием на тайле — последняя из сущностей Pave там, как `paving.at` в TS.
pub fn pave_work_at(world: &World, x: i32, y: i32) -> Option<Entity> {
  world.query(&["Position", "Pave"]).into_iter().filter(|&pave| number(world, pave, "Position", "x") == Some(x as f64) && number(world, pave, "Position", "y") == Some(y as f64)).max()
}

/// Дорога на тайле — готовая или та, что кладут (`isRoadway`).
pub fn is_roadway(sim: &mut Sim, x: i32, y: i32) -> bool {
  let bounds = sim.vision.bounds;
  if Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).is_paved(true, x, y) {
    return true;
  }
  pave_work_at(&sim.world, x, y).is_some_and(|work| {
    let pave = sim.world.get(work, "Pave");
    pave.and_then(|pave| pave.get("kind")).and_then(|kind| kind.as_str()) == Some("road") && !pave.and_then(|pave| pave.get("remove")).and_then(|remove| remove.as_bool()).unwrap_or(false)
  })
}

/// Можно ли поставить здание вида kind с левым верхним тайлом (x, y); gap — свободная полоса вокруг (`canPlace`).
pub fn can_place(sim: &mut Sim, kind: &str, x: f64, y: f64, gap: f64) -> bool {
  if x.fract() != 0.0 || y.fract() != 0.0 || !x.is_finite() || !y.is_finite() {
    return false;
  }
  let spec = &DATA.buildings[kind];
  let bounds = sim.vision.bounds;
  if x < bounds.left as f64 || y < bounds.top as f64 || x + spec.width > bounds.right as f64 || y + spec.height > bounds.bottom as f64 {
    return false;
  }
  let sandy = spec.defense || spec.pipe.is_some();
  let (left, top, width, height) = (x as i32, y as i32, spec.width as i32, spec.height as i32);
  for tile_y in top..top + height {
    for tile_x in left..left + width {
      let terrain = sim.land.tile(tile_x, tile_y)[0];
      let accepts = terrain == ROCK || (sandy && terrain == SAND);
      let foundation = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).is_paved(false, tile_x, tile_y);
      if !accepts && !foundation {
        return false;
      }
      if is_cliff_foot(sim, tile_x, tile_y) || is_roadway(sim, tile_x, tile_y) {
        return false;
      }
    }
  }
  let gap = gap as i32;
  for tile_y in top - gap..top + height + gap {
    for tile_x in left - gap..left + width + gap {
      if sim.occupancy.at(&sim.layers, tile_x, tile_y).is_some() {
        return false;
      }
    }
  }
  // Площадка, которую ещё не начали строить, тайлов не занимает, но место за ней уже закреплено.
  let world = &sim.world;
  let gap = gap as f64;
  for site in world.query(&["Position", "Site"]) {
    let other = &DATA.buildings[text(world, site, "Site", "type").unwrap_or_default()];
    let (site_x, site_y) = (number(world, site, "Position", "x").unwrap_or(0.0), number(world, site, "Position", "y").unwrap_or(0.0));
    if x - gap < site_x + other.width && x + spec.width + gap > site_x && y - gap < site_y + other.height && y + spec.height + gap > site_y {
      return false;
    }
  }
  true
}

/// Наземные юниты, чей центр внутри прямоугольника (`unitsIn`).
pub fn units_in(world: &World, x: f64, y: f64, width: f64, height: f64) -> Vec<Entity> {
  let mut inside = Vec::new();
  for entity in world.query(&["Position", "Unit"]) {
    if flies(text(world, entity, "Unit", "type").unwrap_or_default()) {
      continue;
    }
    let (px, py) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    if px >= x && px < x + width && py >= y && py < y + height {
      inside.push(entity);
    }
  }
  inside
}

/// Отправляет юнитов из основания на свободные тайлы рядом (`evictUnits`).
pub fn evict_units(sim: &mut Sim, x: f64, y: f64, width: f64, height: f64, inside: &[Entity]) {
  if inside.is_empty() {
    return;
  }
  let group: HashSet<Entity> = inside.iter().copied().collect();
  let taken = standing_units(&sim.world, &group, DATA.units["mcv"].radius, false, false);
  let mut passable = group.clone();
  passable.extend(units_in(&sim.world, x - EVICT_MARGIN, y - EVICT_MARGIN, width + EVICT_MARGIN * 2.0, height + EVICT_MARGIN * 2.0));
  let bounds = sim.vision.bounds;
  let tiles = Ground::new(&mut sim.land, &sim.layers, &sim.world, &sim.occupancy, bounds, &sim.rules).free_tiles_near((x + (width / 2.0).floor()) as i32, (y + (height / 2.0).floor()) as i32, inside.len(), 1, Some(&taken), false);
  send_to_tiles(sim, inside, &tiles, &passable);
}

/// Рассылает юнитов по тайлам, каждого в свой; мест меньше — лишние идут в последнее (`sendToTiles`).
pub fn send_to_tiles(sim: &mut Sim, units: &[Entity], tiles: &[i32], group: &HashSet<Entity>) {
  if tiles.is_empty() {
    return;
  }
  for (i, &entity) in units.iter().enumerate() {
    let at = (i * 2).min(tiles.len() - 2);
    order_move(sim, entity, tiles[at], tiles[at + 1], Some(group), 0.0, 0.0, None);
  }
}

/// Освобождает прямоугольник под здание игрока и говорит, свободен ли он (`clearGround`): свои стоящие с evict уходят.
#[allow(clippy::too_many_arguments)]
pub fn clear_ground(sim: &mut Sim, player: f64, x: f64, y: f64, width: f64, height: f64, evict: bool, ignore: Option<Entity>) -> bool {
  let inside: Vec<Entity> = units_in(&sim.world, x, y, width, height).into_iter().filter(|&entity| Some(entity) != ignore).collect();
  if inside.is_empty() {
    return true;
  }
  if evict {
    let own: Vec<Entity> = inside.into_iter().filter(|&entity| is_own(&sim.world, player, entity) && !sim.world.has(entity, "Path")).collect();
    evict_units(sim, x, y, width, height, &own);
  }
  false
}

/// Уничтожает дропы под основанием (`clearDrops`).
pub fn clear_drops(world: &mut World, x: f64, y: f64, width: f64, height: f64) {
  let mut gone = Vec::new();
  for entity in world.query(&["Position", "Drop"]) {
    let (px, py) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    if px >= x && px < x + width && py >= y && py < y + height {
      gone.push(entity);
    }
  }
  for entity in gone {
    world.destroy(entity);
  }
}
