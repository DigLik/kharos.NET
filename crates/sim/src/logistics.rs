//! Склады сети труб (logistics.ts, upgrades.ts): что здание отдаёт по заявкам, сколько его сеть может отдать сразу и
//! как из неё берут.

use crate::data::DATA;
use crate::inventory::Stock;
use crate::systems::production::{materials_for, spare_of};
use crate::topics::research_needs;
use crate::values::{number, text};
use crate::Sim;
use kharos_engine::{Entity, Owner, World};
use serde_json::Value;
use std::sync::LazyLock;

/// Виды месторождений в порядке deposits.json — как DEPOSIT_TYPES в TS: по номеру в нём слой помнит вид.
static DEPOSIT_TYPES: LazyLock<Vec<String>> = LazyLock::new(|| {
  let table: serde_json::Map<String, Value> = serde_json::from_str(include_str!("../../../packages/base/data/deposits.json")).expect("deposits.json");
  table.keys().cloned().collect()
});

/// Хранилище ли это (`isStore`).
pub fn is_store(world: &World, entity: Entity) -> bool {
  text(world, entity, "Building", "type").is_some_and(|kind| DATA.buildings[kind].stores)
}

/// Станция ли это запроса (`isRequester`).
pub fn is_requester(world: &World, entity: Entity) -> bool {
  text(world, entity, "Building", "type").is_some_and(|kind| DATA.buildings[kind].requester)
}

/// Руда месторождения под шахтой (`mineOre`).
pub fn mine_ore(sim: &Sim, mine: Entity) -> Option<&'static str> {
  let (x, y) = (number(&sim.world, mine, "Position", "x")?, number(&sim.world, mine, "Position", "y")?);
  let layer = sim.layers.id("deposits")?;
  let cell = sim.layers.cell_of(layer, x, y);
  let spot = sim.layers.items(layer, Owner::World, cell).into_iter().find(|item| item.x == x && item.y == y)?;
  let kind = DEPOSIT_TYPES.get(*spot.fields.first()? as usize)?;
  DATA.ores.iter().find(|(_, resource)| *resource == kind).map(|(ore, _)| ore.as_str())
}

/// Готовое: ресурсы и изделия, без руды (`WARES`).
fn is_ware(good: &str) -> bool {
  !DATA.ores.contains_key(good)
}

/// Что здание отдаёт по заявкам (`offersOf`), в порядке GOODS.
pub fn offers_of(sim: &Sim, entity: Entity) -> Vec<&'static str> {
  let world = &sim.world;
  let goods = DATA.goods.iter().map(String::as_str);
  // Дроп отдаёт всё, что на нём лежит, станция запроса — всё, что держит.
  if world.has(entity, "Drop") || is_requester(world, entity) {
    let Some(stock) = Stock::of(world, entity) else { return Vec::new() };
    return goods.filter(|good| stock.amount(good) > 1e-9).collect();
  }
  let Some(kind) = text(world, entity, "Building", "type") else { return Vec::new() };
  if world.has(entity, "Site") {
    return Vec::new();
  }
  let spec = &DATA.buildings[kind];
  if spec.stores || spec.refines.is_some() {
    return goods.filter(|good| is_ware(good)).collect();
  }
  // Космопорт: кроме товара открытой продажи и материалов своего производства.
  if spec.trades {
    let order = world.get(entity, "Trade");
    let selling = order.filter(|order| !order.get("buy").and_then(Value::as_bool).unwrap_or(false)).and_then(|order| order.get("resource")).and_then(Value::as_str);
    let materials = materials_for(world, entity);
    let needed = |good: &str| materials.and_then(|materials| materials.get(good)).and_then(Value::as_f64).is_some_and(|amount| amount != 0.0);
    return goods.filter(|good| is_ware(good) && Some(*good) != selling && !needed(good)).collect();
  }
  if spec.assembles.is_some() {
    let assembly = world.get(entity, "Assembly");
    let on = assembly.and_then(|assembly| assembly.get("on")).and_then(Value::as_bool).unwrap_or(false);
    let recipe = assembly.and_then(|assembly| assembly.get("recipe")).and_then(Value::as_str).and_then(|product| DATA.products.get(product));
    let input = |good: &str| on && recipe.is_some_and(|spec| spec.recipe().any(|(resource, _)| resource == good));
    return goods.filter(|good| is_ware(good) && !input(good)).collect();
  }
  // Лаборатория отдаёт всё, что не нужно ни нынешней теме, ни головной.
  if spec.researches {
    let needs = research_needs(sim, number(world, entity, "Owner", "player").unwrap_or(0.0));
    return goods.filter(|good| !needs.iter().any(|(need, amount)| need == good && *amount != 0.0)).collect();
  }
  if !spec.extract {
    return Vec::new();
  }
  mine_ore(sim, entity).into_iter().collect()
}

/// Сколько груза склад отдаёт по заявкам: всё, что можно потратить, а хранилище — сверх своего резерва (`givenBy`).
pub fn given_by(world: &World, entity: Entity, good: &str) -> f64 {
  0f64.max(spare_of(world, entity, good) - number(world, entity, "Inventory", "reserve").unwrap_or(0.0))
}

/// Здания сети труб здания (`sourcesOf` без wide).
fn sources_of(sim: &mut Sim, building: Entity) -> Vec<Entity> {
  sim.zones.network_of(&sim.world, &sim.occupancy, &sim.layers, building).map_or_else(Vec::new, |zone| zone.buildings.clone())
}

/// Сколько груза сеть здания может отдать на его нужды сразу (`networkStock`).
pub fn network_stock(sim: &mut Sim, building: Entity, good: &str) -> f64 {
  let mut amount = 0.0;
  for source in sources_of(sim, building) {
    if offers_of(sim, source).contains(&good) {
      amount += given_by(&sim.world, source, good);
    }
  }
  amount
}

/// Забирает готовое со складов сети здания, сначала из хранилищ (`takeFromNetwork`).
pub fn take_from_network(sim: &mut Sim, building: Entity, materials: &[(String, f64)]) {
  let mut sources = sources_of(sim, building);
  sources.sort_by_key(|&source| !is_store(&sim.world, source));
  for (good, amount) in materials.iter().filter(|(_, amount)| *amount > 0.0) {
    let mut left = *amount;
    for &source in &sources {
      if left <= 1e-9 {
        break;
      }
      if !offers_of(sim, source).contains(&good.as_str()) {
        continue;
      }
      let given = given_by(&sim.world, source, good);
      let Some(mut stock) = Stock::of(&sim.world, source) else { continue };
      left -= stock.take(good, left.min(given));
      stock.save(&mut sim.world, source);
    }
  }
}
