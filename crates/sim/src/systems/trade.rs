//! Торговля с орбиты (trade.ts, `trade`): корабль космопорта улетает, когда привезён весь товар заявки, и за долетевший
//! платят; корабль с закупкой выгружает её в склад космопорта.

use super::production::delivered_to;
use crate::common::reward;
use crate::data::{level_factor, DATA};
use crate::economy::add_credits;
use crate::inventory::Stock;
use crate::math::js_round;
use crate::values::{js_number, number, text};
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::{Map, Value};

/// Сколько секунд летит корабль с продажей (`SELL_SECONDS`).
const SELL_SECONDS: f64 = 20.0;

/// Сколько тиков летит корабль этого космопорта: с уровнем рейс короче (`flightTicks`).
fn flight_ticks(world: &World, port: Entity, seconds: f64, step: f64) -> f64 {
  js_round(seconds / level_factor(number(world, port, "Building", "level").unwrap_or(1.0)) / step)
}

fn set(world: &mut World, port: Entity, fields: &[(&str, f64)]) {
  world.set(port, "Trade", fields.iter().map(|&(name, value)| (name.to_owned(), js_number(value))).collect::<Map<String, Value>>());
}

/// Раз в тик: корабли с полным грузом улетают, а за долетевшие платят.
pub fn trade(sim: &mut Sim, time: Time) {
  let world = &mut sim.world;
  let mut done = Vec::new();
  for entity in world.query(&["Trade", "Owner"]) {
    let total = number(world, entity, "Trade", "total").unwrap_or(0.0);
    if total > 0.0 {
      let left = number(world, entity, "Trade", "left").unwrap_or(0.0) - 1.0;
      set(world, entity, &[("left", left)]);
      if left <= 0.0 {
        done.push((entity, number(world, entity, "Owner", "player").unwrap_or(0.0)));
      }
    } else if delivered_to(world, entity) >= number(world, entity, "Trade", "wanted").unwrap_or(0.0) - 1e-9 {
      let ticks = flight_ticks(world, entity, SELL_SECONDS, time.step);
      set(world, entity, &[("left", ticks), ("total", ticks)]);
    }
  }
  // Корабль улетает с товаром заявки; остальное в складе космопорта остаётся. Закупка — наоборот, выгружается.
  for (port, player) in done {
    let resource = text(world, port, "Trade", "resource").unwrap_or_default().to_owned();
    let wanted = number(world, port, "Trade", "wanted").unwrap_or(0.0);
    let buy = world.get(port, "Trade").and_then(|order| order.get("buy")).and_then(Value::as_bool).unwrap_or(false);
    let mut hold = Stock::of(world, port);
    if buy {
      if let Some(mut hold) = hold {
        hold.put(&resource, wanted);
        hold.save(world, port);
      }
      world.remove(port, "Trade");
      continue;
    }
    let sold = hold.as_mut().map_or(0.0, |hold| hold.take(&resource, wanted));
    if let Some(hold) = hold {
      hold.save(world, port);
    }
    world.remove(port, "Trade");
    let price = DATA.resources.get(&resource).map_or(0.0, |spec| spec.price);
    add_credits(world, player, js_round(sold * price));
    if sold > 0.0 {
      reward(world, player, "sale");
    }
  }
}
