//! Системы симуляции Kharos (docs/engine.md, §4): правила игры поверх движка. Переносятся из `sim-ts` по одной
//! (docs/boundary.md, §4): система на Rust стоит на своём месте в порядке тика, а остальные пока идут в TS.

pub mod data;
pub use kharos_engine::math;
pub mod occupancy;
pub mod systems;
mod values;

use kharos_engine::terrain::{GeneratorConfig, Land, LandArea};
use kharos_engine::{Change, Layers, Owner, World};
use occupancy::Occupancy;

pub use systems::{reads, run, SYSTEMS};

/// Время тика: номер и длина в секундах, как `Time` в TS.
#[derive(Clone, Copy, Debug)]
pub struct Time {
  pub tick: u64,
  pub step: f64,
}

/// Симуляция: мир сущностей, земля и остальные слои (docs/layers.md).
pub struct Sim {
  pub world: World,
  /// Земля — слой `land`: хранит её генератор с ленивой сборкой чанков, а изменения ходят тем же форматом, что у слоёв.
  pub land: Land,
  pub layers: Layers,
  /// Занятость тайлов зданиями — выводимый слой `occupancy`; обновляется в `settle`.
  pub occupancy: Occupancy,
}

impl Sim {
  /// Новая симуляция на земле из генератора config с картой мира area (в чанках).
  pub fn new(config: GeneratorConfig, area: LandArea) -> Self {
    let mut world = World::new();
    let layers = Layers::new(data::DATA.layers.clone());
    let occupancy = Occupancy::new(&mut world, &layers);
    Self { world, land: Land::new(config, area), layers, occupancy }
  }

  /// Доводит выводимые слои до мира: разбирает события наблюдателей по порядку. Зовётся после чужого журнала и после
  /// каждой системы — до того, как их кто-то прочтёт.
  pub fn settle(&mut self) {
    for event in self.world.take_events() {
      self.occupancy.handle(&event, &mut self.layers);
    }
  }

  /// Применяет изменения слоёв от той стороны (зеркало TS, загрузка): чанки земли — в землю, остальное — в слои.
  pub fn apply(&mut self, changes: &[Change]) -> Result<(), String> {
    for change in changes {
      if change.layer == "land" && change.owner == Owner::World {
        self.land.set_chunk(change.x, change.y, &change.bytes)?;
      } else {
        self.layers.apply(change)?;
      }
    }
    Ok(())
  }
}

#[cfg(test)]
mod tests {
  use super::*;
  use kharos_engine::layers::{decode, encode};
  use kharos_engine::terrain::CHUNK_BYTES;

  #[test]
  fn land_chunks_come_as_layer_changes() {
    let config: GeneratorConfig = serde_json::from_str(r#"{"seed":7,"zoneScale":300,"zoneWarp":60,"swampLevel":-0.3,"rockLevel":0.3,"biomeScale":500,"peakChance":0.01,"peakMinRadius":1,"peakMaxRadius":2,"doubleChance":0.3,"tripleChance":0.1,"saltSwampShift":0,"saltRockShift":0,"saltPeakFactor":1,"redSwampShift":0,"redRockShift":0,"redPeakFactor":1,"marshSwampShift":0,"marshRockShift":0,"marshPeakFactor":1,"tierStep":0.2,"cliffScale":40,"cliffShare":0.5}"#).unwrap();
    let mut sim = Sim::new(config, LandArea { left: -1, top: -1, width: 2, height: 2 });
    let bytes = vec![7u8; CHUNK_BYTES];
    let changes = decode(&encode(&[Change { layer: "land".into(), owner: Owner::World, x: 0, y: -1, bytes: bytes.clone() }])).unwrap();
    sim.apply(&changes).unwrap();
    assert_eq!(sim.land.chunk(0, -1), &bytes[..]);
    // Вне карты и не того размера — ошибка, а не тихий пропуск: зеркало разошлось бы молча.
    assert!(sim.apply(&[Change { layer: "land".into(), owner: Owner::World, x: 5, y: 0, bytes: bytes.clone() }]).is_err());
    assert!(sim.apply(&[Change { layer: "land".into(), owner: Owner::World, x: 0, y: 0, bytes: vec![0; 10] }]).is_err());
  }
}
