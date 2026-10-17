//! Моды Kharos (docs/content.md, docs/mods.md). Мод — папка: `mod.luau` и разделы, в разделе вид — файл или папка.
//! Файлы выполняют и хост (правила), и клиент (подача), каждый в своей машине Luau; этот крейт — общее для обоих:
//! машина с песочницей и `require`, порядок модов, загрузка видов и правки.

pub mod content;
pub mod files;
pub mod mods;
pub mod vm;

pub use content::{Content, LoadError, Proto};
pub use files::Files;
pub use mods::{discover, Mod};
pub use vm::Side;
