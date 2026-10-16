//! Таблицы мода base (packages/base/data): то, что из них нужно системам на Rust. Поля, которых системы пока не
//! читают, пропускаются; схему сверяет tests/base.test.ts.

use kharos_engine::LayerSpec;
use serde::Deserialize;
use std::collections::HashMap;
use std::sync::LazyLock;

#[derive(Deserialize)]
pub struct BuildingSpec {
  pub width: f64,
  pub height: f64,
}

#[derive(Deserialize)]
pub struct TurretSpec {
  pub turn: f64,
}

pub struct Data {
  pub buildings: HashMap<String, BuildingSpec>,
  pub turrets: HashMap<String, TurretSpec>,
  /// Слои мода base в порядке объявления (docs/layers.md).
  pub layers: Vec<LayerSpec>,
}

fn table<T: for<'a> Deserialize<'a>>(name: &str, json: &str) -> HashMap<String, T> {
  serde_json::from_str(json).unwrap_or_else(|error| panic!("{name}.json: {error}"))
}

/// Данные мода base, вшитые в сборку.
pub static DATA: LazyLock<Data> = LazyLock::new(|| Data { buildings: table("buildings", include_str!("../../../packages/base/data/buildings.json")), turrets: table("turrets", include_str!("../../../packages/base/data/turrets.json")), layers: LayerSpec::parse_all(include_str!("../../../packages/base/data/layers.json")).unwrap_or_else(|error| panic!("layers.json: {error}")) });

#[cfg(test)]
mod tests {
  use super::DATA;

  #[test]
  fn base_data_parses() {
    let names: Vec<&str> = DATA.layers.iter().map(|spec| spec.name.as_str()).collect();
    assert_eq!(names, ["land", "paving", "occupancy", "sight", "explored", "deposits", "traces"]);
    assert!(DATA.buildings.contains_key("command") && DATA.turrets.contains_key("cannon"));
  }
}
