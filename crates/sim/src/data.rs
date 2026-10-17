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
  /// Переработка: какую руду перерабатывает.
  pub refines: Option<String>,
  /// Сколько энергии даёт (больше нуля) или просит (меньше).
  pub power: Option<f64>,
  /// Радар: на сколько тайлов разведывает вспышками.
  pub scan: Option<f64>,
  /// Генератор щита.
  pub shield: Option<ShieldSpec>,
}

#[derive(Deserialize)]
pub struct ShieldSpec {
  pub radius: f64,
  pub capacity: f64,
  pub regen: f64,
}

/// Насколько уровень здания усиливает его (`levelFactor`).
pub const LEVEL_BONUS: f64 = 0.5;
pub fn level_factor(level: f64) -> f64 {
  1.0 + LEVEL_BONUS * (level - 1.0)
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
  /// Скорость, тайлов в секунду; поворот, радиан в секунду; радиус, тайлов.
  pub speed: f64,
  pub turn: f64,
  pub radius: f64,
  pub sight: Option<f64>,
  pub weapon: Option<String>,
  #[serde(default)]
  pub mounts: Vec<Mount>,
}

#[derive(Deserialize)]
pub struct WeaponSpec {
  pub range: f64,
}

/// Изделие сборочного цеха: рецепт на одну сборку и сколько она даёт.
#[derive(Deserialize)]
pub struct ProductSpec {
  recipe: serde_json::Map<String, serde_json::Value>,
  #[serde(rename = "yield")]
  pub output: f64,
}

impl ProductSpec {
  /// Сырьё рецепта парами ресурс — сколько, без нулей, в порядке записи (`recipeOf`).
  pub fn recipe(&self) -> impl Iterator<Item = (&str, f64)> {
    self.recipe.iter().filter_map(|(resource, amount)| amount.as_f64().filter(|&amount| amount > 0.0).map(|amount| (resource.as_str(), amount)))
  }

  /// Сколько сырья на сборку всего (`totalOf`).
  pub fn total(&self) -> f64 {
    self.recipe().fold(0.0, |sum, (_, amount)| sum + amount)
  }
}

#[derive(Deserialize)]
pub struct OreSpec {
  resource: String,
}

pub struct Data {
  pub buildings: HashMap<String, BuildingSpec>,
  pub turrets: HashMap<String, TurretSpec>,
  pub units: HashMap<String, UnitSpec>,
  /// Всё, что лежит на складах, в порядке GOODS: ресурсы, изделия, руда.
  pub goods: Vec<String>,
  /// Руда → что из неё выходит на переработке.
  pub ores: HashMap<String, String>,
  pub products: HashMap<String, ProductSpec>,
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
/// Ключи таблицы в порядке файла.
fn keys(name: &str, json: &str) -> Vec<String> {
  serde_json::from_str::<serde_json::Map<String, serde_json::Value>>(json).unwrap_or_else(|error| panic!("{name}.json: {error}")).keys().cloned().collect()
}

const RESOURCES: &str = include_str!("../../../packages/base/data/resources.json");
const PRODUCTS: &str = include_str!("../../../packages/base/data/products.json");
const ORES: &str = include_str!("../../../packages/base/data/ores.json");

pub static DATA: LazyLock<Data> = LazyLock::new(|| Data { goods: [keys("resources", RESOURCES), keys("products", PRODUCTS), keys("ores", ORES)].concat(), ores: table::<OreSpec>("ores", ORES).into_iter().map(|(ore, spec)| (ore, spec.resource)).collect(), products: table("products", PRODUCTS), buildings: table("buildings", include_str!("../../../packages/base/data/buildings.json")), turrets: table("turrets", include_str!("../../../packages/base/data/turrets.json")), units: table("units", include_str!("../../../packages/base/data/units.json")), unit_types: serde_json::from_str::<serde_json::Map<String, serde_json::Value>>(include_str!("../../../packages/base/data/units.json")).expect("units.json").keys().cloned().collect(), weapons: table("weapons", include_str!("../../../packages/base/data/weapons.json")), layers: LayerSpec::parse_all(include_str!("../../../packages/base/data/layers.json")).unwrap_or_else(|error| panic!("layers.json: {error}")) });

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
