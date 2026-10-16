//! Память запоминаемого слоя мира (recall.ts, docs/layers.md, С6): слой у всех один, но его изменения игрок узнаёт,
//! только увидев, — в тумане войны тайл остаётся для него таким, каким он его видел последним. Своё и союзное он знает
//! всегда. Так помнятся правки земли и покрытие: одно правило, два слоя (`Kind`).
//!
//! Значение тайла — одно число: у земли — хранимые байты тайла (`Land::stored`) подряд, у покрытия — его код. Помнится
//! только расхождение с общим — с тем, что игрок знает, ничего не видев. Что игрок узнал, уходит событиями (`Event`):
//! по ним TS повторяет память у себя — её читают хост и сохранение.

use crate::vision::{allies_in, Vision};
use kharos_engine::layers::CHUNK;
use kharos_engine::terrain::Land;
use kharos_engine::{LayerId, Layers, Owner, World};
use std::collections::{BTreeMap, BTreeSet};

/// Раз во сколько тиков игроки узнают увиденное: обзор и так пересчитывается раз в столько.
pub const LEARN_TICKS: i64 = 4;
/// Сторона ячейки, по которым разложено несверенное, — как у следов.
pub const RECALL_CELL: i32 = 16;

pub type Tile = (i32, i32);

/// Какой слой помнится.
pub enum Kind {
  /// Земля: общее — тайл до первой правки (`original`), правки ничьи.
  Land,
  /// Покрытие: общее — «покрытия нет», у кода есть хозяин.
  Paving(LayerId),
}

/// Что изменилось в памяти — для зеркала TS.
#[derive(Clone, Debug, PartialEq)]
pub enum Event {
  /// Игроку заведена память.
  Track { player: u32 },
  /// Игрок узнал тайл: value — что он теперь знает иначе, чем общее; None — знает общее.
  Learn { player: u32, tile: Tile, value: Option<u32> },
}

pub struct Recall {
  kind: Kind,
  /// Что знает игрок иначе, чем общее.
  pub known: BTreeMap<u32, BTreeMap<Tile, u32>>,
  /// Где знание игрока может расходиться со слоем — по ячейкам RECALL_CELL: сверяются только ячейки в его обзоре.
  stale: BTreeMap<u32, BTreeMap<Tile, BTreeSet<Tile>>>,
  /// Земля: каким был изменённый тайл до первой правки.
  pub original: BTreeMap<Tile, u32>,
  /// Тайлы слоя, изменившиеся с прошлой сверки.
  changed: BTreeSet<Tile>,
  events: Vec<Event>,
}

/// Хранимые байты тайла одним числом (`packTile`).
pub fn pack(tile: [u8; 4]) -> u32 {
  u32::from_le_bytes(tile)
}

fn cell_of((x, y): Tile) -> Tile {
  (x.div_euclid(RECALL_CELL), y.div_euclid(RECALL_CELL))
}

fn add_stale(stale: &mut BTreeMap<Tile, BTreeSet<Tile>>, tile: Tile) {
  stale.entry(cell_of(tile)).or_default().insert(tile);
}

/// Слои мира, из которых память читает значения.
pub struct Source<'a> {
  pub land: &'a mut Land,
  pub layers: &'a Layers,
  pub world: &'a World,
  pub vision: &'a Vision,
}

impl Recall {
  pub fn new(kind: Kind) -> Self {
    Self { kind, known: BTreeMap::new(), stale: BTreeMap::new(), original: BTreeMap::new(), changed: BTreeSet::new(), events: Vec::new() }
  }

  /// Тайл слоя изменился: его сверят все игроки.
  pub fn change(&mut self, tile: Tile) {
    self.changed.insert(tile);
  }

  /// Знание игрока из сохранения.
  pub fn know(&mut self, player: u32, tile: Tile, value: u32) {
    self.known.entry(player).or_default().insert(tile, value);
  }

  /// Земля: тайл забыт — правка редактора стала исходной картой, её видят все.
  pub fn forget(&mut self, tile: Tile) {
    self.original.remove(&tile);
    for known in self.known.values_mut() {
      known.remove(&tile);
    }
  }

  /// События с прошлого вызова.
  pub fn take_events(&mut self) -> Vec<Event> {
    std::mem::take(&mut self.events)
  }

  fn actual(&self, source: &mut Source, (x, y): Tile) -> u32 {
    match self.kind {
      Kind::Land => pack(source.land.stored(x, y)),
      Kind::Paving(layer) => source.layers.get(layer, Owner::World, x, y, 0) as u32,
    }
  }

  fn common(&self, tile: Tile, actual: u32) -> u32 {
    match self.kind {
      Kind::Land => self.original.get(&tile).copied().unwrap_or(actual),
      Kind::Paving(_) => 0,
    }
  }

  /// Чьё значение; None — ничьё.
  fn owner(&self, value: u32) -> Option<u32> {
    match self.kind {
      Kind::Paving(_) if value != 0 => Some((value - 2) / 2),
      _ => None,
    }
  }

  /// Тайлы, где слой может расходиться с общим: их сверяет новый игрок.
  fn tiles(&self, source: &Source) -> Vec<Tile> {
    match self.kind {
      Kind::Land => self.original.keys().copied().collect(),
      Kind::Paving(layer) => {
        let mut tiles = Vec::new();
        for block in source.layers.blocks(layer, Owner::World) {
          for (i, code) in block.bytes.chunks_exact(2).enumerate() {
            if code != [0, 0] {
              tiles.push((block.x * CHUNK + i as i32 % CHUNK, block.y * CHUNK + i as i32 / CHUNK));
            }
          }
        }
        tiles
      }
    }
  }

  /// Заводит игроку сверку: и того, что он знает, и всего, где слой расходится с общим (`track`).
  fn track(&mut self, source: &Source, player: u32) {
    if self.stale.contains_key(&player) {
      return;
    }
    if !self.known.contains_key(&player) {
      self.known.insert(player, BTreeMap::new());
      self.events.push(Event::Track { player });
    }
    let mut stale = BTreeMap::new();
    for &tile in self.known[&player].keys() {
      add_stale(&mut stale, tile);
    }
    for tile in self.tiles(source) {
      add_stale(&mut stale, tile);
    }
    self.stale.insert(player, stale);
  }

  /// Система тика, после обзора (`updateRecall`): изменившееся ждёт сверки у каждого игрока; своё и союзное он узнаёт
  /// в тот же тик, чужое — увидев, раз в LEARN_TICKS.
  pub fn update(&mut self, source: &mut Source, tick: i64) {
    // Земля без правок: и сверять нечего, и изменённое копится до первой (`updateLandMemory`).
    if matches!(self.kind, Kind::Land) && self.original.is_empty() {
      return;
    }
    let changed: Vec<Tile> = std::mem::take(&mut self.changed).into_iter().collect();
    for stale in self.stale.values_mut() {
      for &tile in &changed {
        add_stale(stale, tile);
      }
    }
    for &player in source.vision.players() {
      self.track(source, player);
    }
    let look = tick % LEARN_TICKS == 0;
    let players: Vec<u32> = self.stale.keys().copied().collect();
    for player in players {
      if self.stale[&player].is_empty() {
        continue;
      }
      if !changed.is_empty() {
        self.learn(source, player, &changed, false);
      }
      if !look {
        continue;
      }
      for cell in source.vision.seen(player, RECALL_CELL) {
        let Some(tiles) = self.stale[&player].get(&cell) else { continue };
        let tiles: Vec<Tile> = tiles.iter().copied().collect();
        self.learn(source, player, &tiles, true);
      }
    }
  }

  /// Игрок узнаёт тайлы tiles из тех, что ждут у него сверки: свои и союзные всегда, чужие — если look и он их видит.
  fn learn(&mut self, source: &mut Source, player: u32, tiles: &[Tile], look: bool) {
    let team = if matches!(self.kind, Kind::Paving(_)) { allies_in(source.world, player) } else { Vec::new() };
    let ours = |owner: Option<u32>| owner.is_some_and(|owner| owner == player || team.contains(&(owner as f64)));
    for &tile in tiles {
      let cell = cell_of(tile);
      if !self.stale[&player].get(&cell).is_some_and(|keys| keys.contains(&tile)) {
        continue;
      }
      let actual = self.actual(source, tile);
      let common = self.common(tile, actual);
      let was = self.known[&player].get(&tile).copied().unwrap_or(common);
      if actual != was {
        if !ours(self.owner(actual)) && !ours(self.owner(was)) && !(look && source.vision.sees(source.layers, player, tile.0 as f64, tile.1 as f64)) {
          continue;
        }
        let known = self.known.get_mut(&player).unwrap();
        let value = if actual == common {
          known.remove(&tile);
          None
        } else {
          known.insert(tile, actual);
          Some(actual)
        };
        self.events.push(Event::Learn { player, tile, value });
      }
      let stale = self.stale.get_mut(&player).unwrap();
      let keys = stale.get_mut(&cell).unwrap();
      keys.remove(&tile);
      if keys.is_empty() {
        stale.remove(&cell);
      }
    }
  }
}
