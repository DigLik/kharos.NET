//! Таблицы мода base (packages/base/data): то, что из них нужно системам на Rust. Поля, которых системы пока не
//! читают, пропускаются; схему сверяет tests/base.test.ts.

use kharos_engine::LayerSpec;
use serde::Deserialize;
use std::collections::HashMap;
use std::sync::LazyLock;

/// Турель на юните или здании: её вид.
#[derive(Deserialize)]
pub struct Mount {
  pub turret: String,
}

#[derive(Deserialize)]
pub struct BuildingSpec {
  pub width: f64,
  pub height: f64,
  pub sight: Option<f64>,
  #[serde(default)]
  pub mounts: Vec<Mount>,
  /// Ворота: см. systems/gates.rs.
  #[serde(default)]
  pub gate: bool,
  /// Реактор: что жжёт и сколько секунд горит единица.
  pub burns: Option<Burns>,
}

#[derive(Deserialize)]
pub struct Burns {
  pub good: String,
  pub seconds: f64,
}

#[derive(Deserialize)]
pub struct TurretSpec {
  pub turn: f64,
  pub weapon: Option<String>,
}

#[derive(Deserialize)]
pub struct UnitSpec {
  /// Класс: infantry, vehicle, heavy, air.
  pub kind: String,
  pub sight: Option<f64>,
  pub weapon: Option<String>,
  #[serde(default)]
  pub mounts: Vec<Mount>,
}

#[derive(Deserialize)]
pub struct WeaponSpec {
  pub range: f64,
}

pub struct Data {
  pub buildings: HashMap<String, BuildingSpec>,
  pub turrets: HashMap<String, TurretSpec>,
  pub units: HashMap<String, UnitSpec>,
  /// Виды юнитов в порядке units.json — как UNIT_TYPES в TS: по номеру в нём следы помнят вид.
  pub unit_types: Vec<String>,
  pub weapons: HashMap<String, WeaponSpec>,
  /// Слои мода base в порядке объявления (docs/layers.md).
  pub layers: Vec<LayerSpec>,
}

fn table<T: for<'a> Deserialize<'a>>(name: &str, json: &str) -> HashMap<String, T> {
  serde_json::from_str(json).unwrap_or_else(|error| panic!("{name}.json: {error}"))
}

/// Данные мода base, вшитые в сборку.
pub static DATA: LazyLock<Data> = LazyLock::new(|| Data { buildings: table("buildings", include_str!("../../../packages/base/data/buildings.json")), turrets: table("turrets", include_str!("../../../packages/base/data/turrets.json")), units: table("units", include_str!("../../../packages/base/data/units.json")), unit_types: serde_json::from_str::<serde_json::Map<String, serde_json::Value>>(include_str!("../../../packages/base/data/units.json")).expect("units.json").keys().cloned().collect(), weapons: table("weapons", include_str!("../../../packages/base/data/weapons.json")), layers: LayerSpec::parse_all(include_str!("../../../packages/base/data/layers.json")).unwrap_or_else(|error| panic!("layers.json: {error}")) });

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
