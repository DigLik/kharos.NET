//! Моды и их порядок. Мод — папка с `mod.luau`: имя, версия, название, от каких модов зависит. Загружаются так, чтобы
//! зависимость шла раньше зависящего; при равенстве — по имени, чтобы порядок был один у хоста и клиентов.

use crate::files::Files;
use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use std::rc::Rc;

/// Мод: имя (из `mod.luau`, иначе — имя папки) и файлы.
#[derive(Clone, Debug)]
pub struct Mod {
  pub name: String,
  pub files: Rc<Files>,
  /// Откуда взят — для сообщений; у присланного по сети пусто.
  pub dir: Option<PathBuf>,
}

impl Mod {
  pub fn read_dir(dir: &Path) -> std::io::Result<Self> {
    let files = Files::read_dir(dir)?;
    let name = dir.file_name().map(|name| name.to_string_lossy().to_string()).unwrap_or_default();
    Ok(Self { name, files: Rc::new(files), dir: Some(dir.to_path_buf()) })
  }
}

/// Моды из папки: каждая подпапка с `mod.luau` — мод.
pub fn discover(root: &Path) -> std::io::Result<Vec<Mod>> {
  let mut mods = Vec::new();
  let mut dirs: Vec<PathBuf> = std::fs::read_dir(root)?.filter_map(|entry| entry.ok().map(|entry| entry.path())).filter(|path| path.join("mod.luau").is_file()).collect();
  dirs.sort();
  for dir in dirs {
    mods.push(Mod::read_dir(&dir)?);
  }
  Ok(mods)
}

/// Порядок загрузки: зависимости раньше, при равенстве — по имени. requires — имя мода → от чего зависит (`?имя` —
/// необязательная зависимость). Нет обязательной зависимости или зависимости по кругу — ошибка.
pub fn order(requires: &BTreeMap<String, Vec<String>>) -> Result<Vec<String>, String> {
  let mut done: Vec<String> = Vec::new();
  let mut left: BTreeSet<&String> = requires.keys().collect();
  while !left.is_empty() {
    let ready = left.iter().copied().find(|name| {
      requires[*name].iter().all(|need| {
        let (optional, need) = need.strip_prefix('?').map_or((false, need.as_str()), |need| (true, need));
        done.iter().any(|loaded| loaded == need) || (optional && !requires.contains_key(need))
      })
    });
    let Some(name) = ready else {
      let stuck: Vec<String> = left.iter().map(|name| format!("{name} (нужны: {})", requires[*name].join(", "))).collect();
      let missing: Vec<&String> = left.iter().flat_map(|name| requires[*name].iter()).filter(|need| !need.starts_with('?') && !requires.contains_key(*need)).collect();
      return Err(if missing.is_empty() { format!("моды зависят друг от друга по кругу: {}", stuck.join("; ")) } else { format!("нет модов: {}", missing.iter().map(|name| name.as_str()).collect::<Vec<_>>().join(", ")) });
    };
    left.remove(name);
    done.push(name.clone());
  }
  Ok(done)
}

#[cfg(test)]
mod tests {
  use super::*;

  fn map(pairs: &[(&str, &[&str])]) -> BTreeMap<String, Vec<String>> {
    pairs.iter().map(|(name, needs)| (name.to_string(), needs.iter().map(|need| need.to_string()).collect())).collect()
  }

  #[test]
  fn dependencies_go_first_ties_by_name() {
    assert_eq!(order(&map(&[("zeta", &["base"]), ("alpha", &["zeta"]), ("base", &[]), ("beta", &["base", "?missing"])])).unwrap(), ["base", "beta", "zeta", "alpha"]);
    assert!(order(&map(&[("a", &["b"]), ("b", &["a"])])).unwrap_err().contains("по кругу"));
    assert!(order(&map(&[("a", &["nope"])])).unwrap_err().contains("nope"));
  }
}
