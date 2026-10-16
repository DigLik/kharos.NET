//! Занятость тайлов зданиями (`createOccupancy` в buildings.ts) — выводимый слой `occupancy`: номер здания на тайле,
//! 0 — свободен. Следит за зданиями наблюдателем мира: основание берётся на миг появления здания, как в TS, а при уходе
//! тайл освобождается, только если на нём всё ещё это здание.

use crate::data::DATA;
use kharos_engine::{Entity, Event, LayerId, Layers, Owner, World};
use std::collections::HashMap;

pub struct Occupancy {
  observer: usize,
  layer: LayerId,
  /// Тайлы основания каждого здания — какими их заняли.
  footprints: HashMap<Entity, Vec<(i32, i32)>>,
}

impl Occupancy {
  pub fn new(world: &mut World, layers: &Layers) -> Self {
    let layer = layers.id("occupancy").expect("нет слоя occupancy");
    Self { observer: world.observe(&["Position", "Building"]), layer, footprints: HashMap::new() }
  }

  /// Здание на тайле.
  pub fn at(&self, layers: &Layers, x: i32, y: i32) -> Option<Entity> {
    let entity = layers.get(self.layer, Owner::World, x, y, 0) as Entity;
    (entity != 0).then_some(entity)
  }

  /// Разбирает событие мира; чужие пропускает.
  pub fn handle(&mut self, event: &Event, layers: &mut Layers) {
    match event {
      Event::Enter { observer, entity, data } if *observer == self.observer => {
        let field = |at: usize, name: &str| data[at].get(name).and_then(|value| value.as_f64()).unwrap_or(0.0);
        let (x, y) = (field(0, "x"), field(0, "y"));
        let Some(spec) = data[1].get("type").and_then(|value| value.as_str()).and_then(|kind| DATA.buildings.get(kind)) else { return };
        let mut tiles = Vec::new();
        let mut tile_y = y;
        while tile_y < y + spec.height {
          let mut tile_x = x;
          while tile_x < x + spec.width {
            tiles.push((tile_x.floor() as i32, tile_y.floor() as i32));
            tile_x += 1.0;
          }
          tile_y += 1.0;
        }
        for &(tile_x, tile_y) in &tiles {
          layers.set(self.layer, Owner::World, tile_x, tile_y, 0, *entity as f64);
        }
        self.footprints.insert(*entity, tiles);
      }
      Event::Leave { observer, entity } if *observer == self.observer => {
        for (tile_x, tile_y) in self.footprints.remove(entity).unwrap_or_default() {
          if self.at(layers, tile_x, tile_y) == Some(*entity) {
            layers.set(self.layer, Owner::World, tile_x, tile_y, 0, 0.0);
          }
        }
      }
      _ => {}
    }
  }
}
