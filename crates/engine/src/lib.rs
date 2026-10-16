//! Движок симуляции Kharos (docs/engine.md, §4): то, что не знает правил игры, а даёт им место и инструменты. Сейчас —
//! хранилище мира с журналом, через который его зеркалит TS (docs/boundary.md).

pub mod world;

pub use world::{Entity, Op, World};
