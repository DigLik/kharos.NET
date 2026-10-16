//! Системы симуляции Kharos (docs/engine.md, §4): правила игры поверх движка. Переносятся из `sim-ts` по одной
//! (docs/boundary.md, §4): система на Rust стоит на своём месте в порядке тика, а остальные пока идут в TS.

pub mod data;
pub use kharos_engine::math;
pub mod systems;
mod values;

pub use systems::{reads, run, SYSTEMS};

/// Время тика: номер и длина в секундах, как `Time` в TS.
#[derive(Clone, Copy, Debug)]
pub struct Time {
  pub tick: u64,
  pub step: f64,
}
