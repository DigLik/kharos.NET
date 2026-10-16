//! Таблицы мода base (packages/base/data): то, что из них нужно системам на Rust. Поля, которых системы пока не
//! читают, пропускаются; схему сверяет tests/base.test.ts.

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
}

fn table<T: for<'a> Deserialize<'a>>(name: &str, json: &str) -> HashMap<String, T> {
  serde_json::from_str(json).unwrap_or_else(|error| panic!("{name}.json: {error}"))
}

/// Данные мода base, вшитые в сборку.
pub static DATA: LazyLock<Data> = LazyLock::new(|| Data { buildings: table("buildings", include_str!("../../../packages/base/data/buildings.json")), turrets: table("turrets", include_str!("../../../packages/base/data/turrets.json")) });
