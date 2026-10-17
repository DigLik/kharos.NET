//! Доход и износ (income.ts, `earn`): игроки получают доход своих зон, перегруженные электростанции теряют прочность.
//! Хозяйства тика считает TS: обёртка досылает их перед системой (`sim_books`).

use crate::economy::power_at;
use super::combat::meltdown;
use crate::values::{js_number, number};
use crate::{Sim, Time};
use kharos_engine::Entity;
use serde_json::{Map, Value};

/// Какую долю прочности в секунду теряет электростанция при двойном перегрузе (`OVERLOAD_DAMAGE`).
const OVERLOAD_DAMAGE: f64 = 0.01;

/// Зона с хозяйством тика (`Book`): чья, доход, перегруз и здания.
#[derive(Clone, Debug, Default)]
pub struct Book {
  pub player: f64,
  pub income: f64,
  pub overload: f64,
  pub buildings: Vec<Entity>,
}

pub fn earn(sim: &mut Sim, time: Time) {
  let all = std::mem::take(&mut sim.books);
  if all.is_empty() {
    return;
  }
  let mut incomes: Vec<(f64, f64)> = Vec::new();
  for book in &all {
    match incomes.iter_mut().find(|(player, _)| *player == book.player) {
      Some((_, income)) => *income += book.income,
      None => incomes.push((book.player, book.income)),
    }
  }
  let (share, minutes) = (sim.rule("offlineIncome"), sim.rule("offlineMinutes"));
  let world = &mut sim.world;
  let mut paid = Vec::new();
  for entity in world.query(&["Player"]) {
    let id = number(world, entity, "Player", "id").unwrap_or(0.0);
    let here = sim.online.as_ref().is_none_or(|online| online.contains(&(id as u64)));
    let income = incomes.iter().find(|(player, _)| *player == id).map_or(0.0, |(_, income)| *income);
    let away_before = number(world, entity, "Player", "away").unwrap_or(0.0);
    let mut gain = income * time.step;
    if !here {
      gain = 0f64.max((gain * share).min(income * minutes * 60.0 - away_before));
    }
    let away = if here { 0.0 } else { away_before + gain };
    if gain <= 0.0 && away == away_before {
      continue;
    }
    let earned = number(world, entity, "Player", "earned").unwrap_or(0.0) + gain;
    let whole = earned.floor();
    let credits = number(world, entity, "Player", "credits").unwrap_or(0.0) + whole;
    paid.push((entity, credits, earned - whole, away));
  }
  for (entity, credits, earned, away) in paid {
    let fields = [("credits", credits), ("earned", earned), ("away", away)];
    world.set(entity, "Player", fields.into_iter().map(|(name, value)| (name.to_owned(), js_number(value))).collect::<Map<String, Value>>());
  }
  // Износ — в самом конце: разрушенное здание исчезает из мира, а зоны этого тика о нём ещё помнят.
  let mut ruined = Vec::new();
  for book in &all {
    if book.overload == 0.0 {
      continue;
    }
    for &entity in &book.buildings {
      if !world.has(entity, "Building") || power_at(world, entity) <= 0.0 {
        continue;
      }
      let value = number(world, entity, "Health", "value").unwrap_or(0.0) - OVERLOAD_DAMAGE * book.overload * time.step;
      world.set(entity, "Health", Map::from_iter([("value".to_owned(), js_number(value))]));
      if value <= 0.0 {
        ruined.push(entity);
      }
    }
  }
  for entity in ruined {
    meltdown(world, entity);
    world.destroy(entity);
  }
}
