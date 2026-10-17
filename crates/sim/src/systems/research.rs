//! Исследования (research.ts, `research`): лаборатории ведут нынешнюю тему своего хозяина — головную тему плана или ту,
//! что её подменяет, пока головной не из чего работать, — и платят за сделанное кредитами и сырьём из своей сети.

use crate::common::{is_own, player_entity};
use crate::components;
use crate::data::DATA;
use crate::economy::spend;
use crate::inventory::Stock;
use crate::logistics::{network_stock, take_from_network};
use crate::math::js_round;
use crate::topics::{current_topic, is_topic, open_topics, topic_price, topics_before};
use crate::values::{js_number, number, text};
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::{json, Map, Value};
use std::collections::HashMap;

const EPSILON: f64 = 1e-9;
/// Сколько секунд головная тема стоит без сырья, прежде чем лаборатории возьмутся за следующую (`STALL_SECONDS`).
const STALL_SECONDS: f64 = 10.0;
/// Как часто сверяется, есть ли темам сырьё, в секундах (`PICK_SECONDS`).
const PICK_SECONDS: f64 = 1.0;

/// Как давно головная тема игрока без сырья: head — о какой теме речь, since — с какого тика (-1 — сырьё есть),
/// checked — когда сверяли.
pub struct Stall {
  pub head: String,
  pub since: i64,
  pub checked: i64,
}

/// Готовая лаборатория игрока (`isLab`).
fn is_lab(world: &World, player: f64, entity: Entity) -> bool {
  text(world, entity, "Building", "type").is_some_and(|kind| DATA.buildings[kind].researches) && !world.has(entity, "Site") && is_own(world, player, entity)
}

/// Есть ли под рукой хоть у одной из лабораторий всё сырьё темы (`supplied`).
fn supplied(sim: &mut Sim, topic: &str, labs: &[Entity]) -> bool {
  let goods: Vec<String> = topic_price(sim, topic).materials.into_iter().map(|(good, _)| good).collect();
  if goods.is_empty() {
    return true;
  }
  labs.iter().any(|&lab| goods.iter().all(|good| Stock::of(&sim.world, lab).map_or(0.0, |stock| stock.amount(good)) + network_stock(sim, lab, good) > EPSILON))
}

/// Выбирает тему, которую лаборатории игрока ведут сейчас, и запоминает выбор (`pickTopic`).
pub fn pick_topic(sim: &mut Sim, player: f64, time: Time) -> Option<String> {
  let key = player as i64;
  let open = open_topics(&sim.world, player);
  let Some(head) = open.first().cloned() else {
    sim.research_stalls.remove(&key);
    sim.research_picks.remove(&key);
    return None;
  };
  let tick = time.tick as i64;
  let last = sim.research_stalls.get(&key);
  let same = last.is_some_and(|last| last.head == head);
  if let Some(last) = last.filter(|_| same) {
    if tick >= last.checked && ((tick - last.checked) as f64) * time.step < PICK_SECONDS {
      return current_topic(sim, player);
    }
  }
  let last_since = last.map_or(-1, |last| last.since);
  let mut labs = Vec::new();
  for entity in sim.world.query(&["Building"]) {
    if is_lab(&sim.world, player, entity) && !sim.world.has(entity, "Off") {
      labs.push(entity);
    }
  }
  let since = if supplied(sim, &head, &labs) {
    -1
  } else if same && last_since >= 0 && last_since <= tick {
    last_since
  } else {
    tick
  };
  sim.research_stalls.insert(key, Stall { head, since, checked: tick });
  let stalled = since >= 0 && ((tick - since) as f64) * time.step >= STALL_SECONDS;
  let pick = if stalled { open[1..].iter().find(|topic| supplied(sim, topic, &labs)).cloned() } else { None };
  match pick {
    Some(topic) => sim.research_picks.insert(key, topic),
    None => sim.research_picks.remove(&key),
  };
  current_topic(sim, player)
}

/// Ставит лаборатории её состояние, только если оно изменилось (`report`).
fn report(world: &mut World, lab: Entity, speed: f64, lack: &str) {
  let rounded = js_round(speed * 100.0) / 100.0;
  if !world.has(lab, "Research") {
    world.add(lab, "Research", components::make("Research", [("speed", js_number(rounded)), ("lack", json!(lack))]));
  } else if number(world, lab, "Research", "speed") != Some(rounded) || text(world, lab, "Research", "lack") != Some(lack) {
    world.set(lab, "Research", Map::from_iter([("speed".to_owned(), js_number(rounded)), ("lack".to_owned(), json!(lack))]));
  }
}

fn player_list(world: &World, entity: Entity, field: &str) -> Vec<Value> {
  world.get(entity, "Player").and_then(|player| player.get(field)).and_then(Value::as_array).cloned().unwrap_or_default()
}

/// Первая тема плана, которую можно вести; открытые и недоступные план сбрасывает (`nextTopic`).
fn next_topic(world: &mut World, entity: Entity) -> Option<String> {
  let plan = player_list(world, entity, "plan");
  let known: Vec<String> = player_list(world, entity, "research").iter().filter_map(|topic| topic.as_str().map(str::to_owned)).collect();
  let usable = |topic: &Value| topic.as_str().is_some_and(|topic| is_topic(topic) && !known.iter().any(|done| done == topic) && topics_before(topic).iter().all(|before| known.contains(before)));
  let skip = plan.iter().position(usable).unwrap_or(plan.len());
  if skip > 0 {
    world.set(entity, "Player", Map::from_iter([("plan".to_owned(), Value::Array(plan[skip..].to_vec()))]));
  }
  plan.get(skip).and_then(Value::as_str).map(str::to_owned)
}

/// Записывает прогресс темы; законченная переходит в открытые и уходит из плана (`finish`).
fn finish(world: &mut World, entity: Entity, topic: &str, done: f64) {
  let mut progress = world.get(entity, "Player").and_then(|player| player.get("progress")).and_then(Value::as_object).cloned().unwrap_or_default();
  if done < 1.0 - EPSILON {
    if progress.get(topic).and_then(Value::as_f64) != Some(done) {
      progress.insert(topic.to_owned(), js_number(done));
      world.set(entity, "Player", Map::from_iter([("progress".to_owned(), Value::Object(progress))]));
    }
    return;
  }
  progress.remove(topic);
  let mut known = player_list(world, entity, "research");
  known.push(json!(topic));
  let plan: Vec<Value> = player_list(world, entity, "plan").into_iter().filter(|next| next.as_str() != Some(topic)).collect();
  world.set(entity, "Player", Map::from_iter([("progress".to_owned(), Value::Object(progress)), ("research".to_owned(), Value::Array(known)), ("plan".to_owned(), Value::Array(plan))]));
}

/// Ключ склада сети лаборатории: лаборатории одной сети спрашивают одно и то же.
#[derive(Clone, PartialEq, Eq, Hash)]
enum Network {
  Zone(Entity),
  Lab(Entity),
}

pub fn research(sim: &mut Sim, time: Time) {
  let mut labs: Vec<(f64, Vec<Entity>)> = Vec::new();
  for entity in sim.world.query(&["Building", "Owner"]) {
    let world = &sim.world;
    if !DATA.buildings[text(world, entity, "Building", "type").unwrap_or_default()].researches || world.has(entity, "Site") {
      continue;
    }
    let player = number(world, entity, "Owner", "player").unwrap_or(0.0);
    match labs.iter_mut().find(|(owner, _)| *owner == player) {
      Some((_, list)) => list.push(entity),
      None => labs.push((player, vec![entity])),
    }
  }
  if labs.is_empty() {
    return;
  }
  // Что отдают склады сети лаборатории: считается раз на сеть и груз, взятое вычитается.
  let mut stocks: HashMap<Network, HashMap<String, f64>> = HashMap::new();
  for (player, list) in labs {
    let entity = player_entity(&sim.world, player);
    let topic = match entity {
      Some(entity) if next_topic(&mut sim.world, entity).is_some() => pick_topic(sim, player, time),
      _ => None,
    };
    let (Some(entity), Some(topic)) = (entity, topic) else {
      for lab in list {
        report(&mut sim.world, lab, 0.0, "idle");
      }
      continue;
    };
    let price = topic_price(sim, &topic);
    let ticks = 1f64.max(price.seconds / time.step);
    let mut done = sim.world.get(entity, "Player").and_then(|data| data.get("progress")).and_then(|progress| progress.get(&topic)).and_then(Value::as_f64).unwrap_or(0.0);
    for lab in list {
      // Выключенная энергию не берёт, но стоит не от нехватки.
      if sim.world.has(lab, "Off") {
        report(&mut sim.world, lab, 0.0, "off");
        continue;
      }
      let power = sim.power.get(&lab).copied().unwrap_or(0.0);
      let share = (power / ticks).min(1.0 - done);
      if share <= EPSILON {
        report(&mut sim.world, lab, 0.0, if power > 0.0 { "idle" } else { "power" });
        continue;
      }
      // Какую долю своей работы лаборатория может оплатить: всю или сколько хватает самого скудного.
      let mut part = 1.0;
      let mut lack = if power < 1.0 { "power".to_owned() } else { String::new() };
      let credits = number(&sim.world, entity, "Player", "credits").unwrap_or(0.0);
      let earned = number(&sim.world, entity, "Player", "earned").unwrap_or(0.0);
      let cost = price.cost * share;
      if cost > EPSILON && credits + earned < cost {
        part = 0f64.max((credits + earned) / cost);
        lack = "credits".to_owned();
      }
      let key = match sim.zones.network_of(&sim.world, &sim.occupancy, &sim.layers, lab) {
        Some(zone) => Network::Zone(zone.buildings.first().copied().unwrap_or(lab)),
        None => Network::Lab(lab),
      };
      let mut needs: Vec<(String, f64)> = Vec::new();
      for (good, amount) in &price.materials {
        let need = amount * share;
        // Сначала — привезённое на склад лаборатории, потом — что отдают склады её сети.
        let own = Stock::of(&sim.world, lab).map_or(0.0, |stock| stock.amount(good));
        let network = match stocks.get(&key).and_then(|stock| stock.get(good)) {
          Some(&amount) => amount,
          None => {
            let amount = network_stock(sim, lab, good);
            stocks.entry(key.clone()).or_default().insert(good.clone(), amount);
            amount
          }
        };
        let have = own + network;
        if have + EPSILON < need * part {
          part = 0f64.max(have / need);
          lack = good.clone();
        }
        needs.push((good.clone(), need));
      }
      if part <= EPSILON {
        report(&mut sim.world, lab, 0.0, if lack.is_empty() { "credits" } else { &lack });
        continue;
      }
      for (_, need) in &mut needs {
        *need *= part;
      }
      if cost > EPSILON && !spend(&mut sim.world, player, (cost * part).min(credits + earned)) {
        report(&mut sim.world, lab, 0.0, "credits");
        continue;
      }
      if let Some(mut stock) = Stock::of(&sim.world, lab) {
        for (good, need) in &mut needs {
          *need -= stock.take(good, *need);
        }
        stock.save(&mut sim.world, lab);
      }
      take_from_network(sim, lab, &needs);
      if let Some(network) = stocks.get_mut(&key) {
        for (good, need) in &needs {
          if let Some(amount) = network.get_mut(good) {
            *amount = 0f64.max(*amount - need);
          }
        }
      }
      done += share * part;
      report(&mut sim.world, lab, power * part, &lack);
    }
    finish(&mut sim.world, entity, &topic, done);
  }
}
