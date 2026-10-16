//! Системы симуляции Kharos (docs/engine.md, §4): правила игры поверх движка. Переносятся из `sim-ts` по одной
//! (docs/boundary.md, §4): система на Rust стоит на своём месте в порядке тика, а остальные пока идут в TS.

pub mod build_memory;
pub mod data;
pub mod inventory;
pub use kharos_engine::math;
pub mod occupancy;
pub mod recall;
pub mod systems;
pub mod traces;
mod values;
pub mod vision;

use kharos_engine::terrain::{GeneratorConfig, Land, LandArea};
use kharos_engine::{Change, Entity, Layers, Owner, World};
use std::collections::HashMap;
use build_memory::BuildMemory;
use occupancy::Occupancy;
use recall::{Kind, Recall};
use traces::Traces;
use vision::{Bounds, Vision};

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
  /// Обзор игроков — слои игрока `sight` и `explored`.
  pub vision: Vision,
  /// Что игроки знают о правках земли и о покрытии (recall.rs).
  pub land_memory: Recall,
  pub pave_memory: Recall,
  /// Какими игроки помнят чужие здания (build_memory.rs).
  pub build_memory: BuildMemory,
  /// Следы на земле — предметы слоя `traces` (traces.rs).
  pub traces: Traces,
  /// На какую долю от полной скорости работает каждый потребитель энергии (`powerSupply`). Хозяйства зон пока считает
  /// TS: она досылает это перед системами, которым нужна энергия (docs/boundary.md).
  pub power: HashMap<Entity, f64>,
  /// Правила мира (`Rules` в @kharos/data/rules) — как их прислал TS.
  pub rules: serde_json::Value,
}

impl Sim {
  /// Новая симуляция на земле из генератора config с картой мира area (в чанках); bounds — границы карты в тайлах,
  /// fog — есть ли туман войны.
  pub fn new(config: GeneratorConfig, area: LandArea, bounds: Bounds, fog: bool) -> Self {
    let mut world = World::new();
    let layers = Layers::new(data::DATA.layers.clone());
    let occupancy = Occupancy::new(&mut world, &layers);
    let vision = Vision::new(&layers, bounds, fog);
    let pave_memory = Recall::new(Kind::Paving(layers.id("paving").expect("нет слоя paving")));
    let traces = Traces::new(&layers);
    Self { world, land: Land::new(config, area), layers, occupancy, vision, land_memory: Recall::new(Kind::Land), pave_memory, build_memory: BuildMemory::default(), traces, power: HashMap::new(), rules: serde_json::Value::Null }
  }

  /// Доводит выводимые слои до мира: разбирает события наблюдателей по порядку. Зовётся после чужого журнала и после
  /// каждой системы — до того, как их кто-то прочтёт.
  pub fn settle(&mut self) {
    for event in self.world.take_events() {
      self.occupancy.handle(&event, &mut self.layers);
    }
  }

  /// Число из правил мира; нет такого — ноль.
  pub fn rule(&self, name: &str) -> f64 {
    self.rules.get(name).and_then(serde_json::Value::as_f64).unwrap_or(0.0)
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

  fn config() -> GeneratorConfig {
    serde_json::from_str(r#"{"seed":7,"zoneScale":300,"zoneWarp":60,"swampLevel":-0.3,"rockLevel":0.3,"biomeScale":500,"peakChance":0.01,"peakMinRadius":1,"peakMaxRadius":2,"doubleChance":0.3,"tripleChance":0.1,"saltSwampShift":0,"saltRockShift":0,"saltPeakFactor":1,"redSwampShift":0,"redRockShift":0,"redPeakFactor":1,"marshSwampShift":0,"marshRockShift":0,"marshPeakFactor":1,"tierStep":0.2,"cliffScale":40,"cliffShare":0.5}"#).unwrap()
  }

  #[test]
  fn land_chunks_come_as_layer_changes() {
    let mut sim = Sim::new(config(), LandArea { left: -1, top: -1, width: 2, height: 2 }, Bounds { left: -32, top: -32, right: 32, bottom: 32 }, true);
    let bytes = vec![7u8; CHUNK_BYTES];
    let changes = decode(&encode(&[Change { layer: "land".into(), owner: Owner::World, x: 0, y: -1, bytes: bytes.clone() }])).unwrap();
    sim.apply(&changes).unwrap();
    assert_eq!(sim.land.chunk(0, -1), &bytes[..]);
    // Вне карты и не того размера — ошибка, а не тихий пропуск: зеркало разошлось бы молча.
    assert!(sim.apply(&[Change { layer: "land".into(), owner: Owner::World, x: 5, y: 0, bytes: bytes.clone() }]).is_err());
    assert!(sim.apply(&[Change { layer: "land".into(), owner: Owner::World, x: 0, y: 0, bytes: vec![0; 10] }]).is_err());
  }

  #[test]
  fn players_learn_own_paving_at_once_and_foreign_on_sight() {
    use recall::Event::Learn;
    use serde_json::json;
    let mut sim = Sim::new(config(), LandArea { left: -2, top: -2, width: 4, height: 4 }, Bounds { left: -64, top: -64, right: 64, bottom: 64 }, true);
    // Вспышки радаров: игрок 1 видит круг у (0, 0), игрок 2 — у (40, 40).
    for (player, at) in [(1, 0), (2, 40)] {
      let entity = sim.world.spawn();
      sim.world.add(entity, "Owner", json!({ "player": player }));
      sim.world.add(entity, "Position", json!({ "x": at, "y": at }));
      sim.world.add(entity, "Scan", json!({ "x": at, "y": at, "tick": 0 }));
    }
    let paving = sim.layers.id("paving").unwrap();
    // Покрытие игрока 2 (код 2·2 + 2): у (1, 1) — на виду у игрока 1, у (41, 41) — нет.
    for tile in [(1, 1), (41, 41)] {
      sim.layers.set(paving, Owner::World, tile.0, tile.1, 0, 6.0);
      sim.pave_memory.change(tile);
    }
    let tick = |sim: &mut Sim, tick| {
      for name in ["vision", "updatePaveMemory"] {
        systems::run(sim, name, Time { tick, step: 0.05 });
      }
      let mut events = sim.pave_memory.take_events();
      events.retain(|event| matches!(event, Learn { .. }));
      events
    };
    // Своё — сразу, чужое — не на тике сверки.
    assert_eq!(tick(&mut sim, 1), [Learn { player: 2, tile: (1, 1), value: Some(6) }, Learn { player: 2, tile: (41, 41), value: Some(6) }]);
    assert_eq!(tick(&mut sim, 2), []);
    // На тике сверки — только то, что на виду.
    assert_eq!(tick(&mut sim, 4), [Learn { player: 1, tile: (1, 1), value: Some(6) }]);
    // Покрытие сняли: хозяин узнаёт сразу и то, что его больше нет.
    sim.layers.set(paving, Owner::World, 41, 41, 0, 0.0);
    sim.pave_memory.change((41, 41));
    assert_eq!(tick(&mut sim, 5), [Learn { player: 2, tile: (41, 41), value: None }]);
    assert_eq!(sim.pave_memory.known[&1].len(), 1);
  }
}
