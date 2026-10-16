//! Движок симуляции Kharos (docs/engine.md, §4): то, что не знает правил игры, а даёт им место и инструменты. Сейчас —
//! хранилище мира с журналом, через который его зеркалит TS (docs/boundary.md), и слои (docs/layers.md).

pub mod layers;
pub mod math;
pub mod terrain;
pub mod world;

pub use layers::{Change, Channel, Item, LayerId, LayerSpec, Layers, Owner, Scope, Shape};
pub use world::{Entity, Event, Op, World};
