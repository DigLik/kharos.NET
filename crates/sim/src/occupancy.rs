//! Занятость тайлов зданиями (`createOccupancy` в buildings.ts) — выводимый слой `occupancy`: номер здания на тайле,
//! 0 — свободен. Следит за зданиями наблюдателем мира: основание берётся на миг появления здания, как в TS, а при уходе
//! тайл освобождается, только если на нём всё ещё это здание. Ещё — здания по ячейкам CELL (`inCell`): так их в обзоре
//! игрока находят по ячейкам обзора, а не перебором всех.

use crate::data::DATA;
use kharos_engine::{Entity, Event, LayerId, Layers, Owner, World};
use std::collections::HashMap;

/// Сторона ячейки, по которым разложены здания, — как у следов и ячеек обзора.
pub const CELL: i32 = 16;

pub struct Occupancy {
  observer: usize,
  layer: LayerId,
  /// Тайлы основания и ячейки каждого здания — какими их заняли.
  footprints: HashMap<Entity, (Vec<(i32, i32)>, Vec<(i32, i32)>)>,
  /// Здания, основание которых задевает ячейку, в порядке появления.
  cells: HashMap<(i32, i32), Vec<Entity>>,
}

impl Occupancy {
  pub fn new(world: &mut World, layers: &Layers) -> Self {
    let layer = layers.id("occupancy").expect("нет слоя occupancy");
    Self { observer: world.observe(&["Position", "Building"]), layer, footprints: HashMap::new(), cells: HashMap::new() }
  }

  /// Здание на тайле.
  pub fn at(&self, layers: &Layers, x: i32, y: i32) -> Option<Entity> {
    let entity = layers.get(self.layer, Owner::World, x, y, 0) as Entity;
    (entity != 0).then_some(entity)
  }

  /// Здания, основание которых задевает ячейку CELL.
  pub fn in_cell(&self, cell: (i32, i32)) -> &[Entity] {
    self.cells.get(&cell).map_or(&[], Vec::as_slice)
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
        let cell = |at: f64| (at / CELL as f64).floor() as i32;
        let mut spots = Vec::new();
        for cell_y in cell(y)..=cell(y + spec.height - 1.0) {
          for cell_x in cell(x)..=cell(x + spec.width - 1.0) {
            spots.push((cell_x, cell_y));
            self.cells.entry((cell_x, cell_y)).or_default().push(*entity);
          }
        }
        self.footprints.insert(*entity, (tiles, spots));
      }
      Event::Leave { observer, entity } if *observer == self.observer => {
        let (tiles, spots) = self.footprints.remove(entity).unwrap_or_default();
        for (tile_x, tile_y) in tiles {
          if self.at(layers, tile_x, tile_y) == Some(*entity) {
            layers.set(self.layer, Owner::World, tile_x, tile_y, 0, 0.0);
          }
        }
        for spot in spots {
          let Some(found) = self.cells.get_mut(&spot) else { continue };
          found.retain(|other| other != entity);
          if found.is_empty() {
            self.cells.remove(&spot);
          }
        }
      }
      _ => {}
    }
  }
}
