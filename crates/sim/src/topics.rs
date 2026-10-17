//! Темы исследований (topics.ts): предшественники, ярус сложности, цена по правилам мира, план игрока и тема, которую
//! его лаборатории ведут сейчас.

use crate::common::player_entity;
use crate::math::{js_pow, js_round};
use crate::Sim;
use kharos_engine::World;
use serde::Deserialize;
use serde_json::Value;
use std::sync::LazyLock;

#[derive(Deserialize)]
pub struct TopicSpec {
  #[serde(default)]
  pub after: Vec<String>,
  pub uses: Option<serde_json::Map<String, Value>>,
  pub trigger: Option<String>,
}

/// Темы мода base (research.json) в порядке файла, как TOPICS в TS.
pub static TOPICS: LazyLock<Vec<(String, TopicSpec)>> = LazyLock::new(|| {
  let table: serde_json::Map<String, Value> = serde_json::from_str(include_str!("../../../packages/base/data/research.json")).expect("research.json");
  table.into_iter().map(|(name, spec)| (name.clone(), serde_json::from_value(spec).unwrap_or_else(|error| panic!("research.json, {name}: {error}")))).collect()
});

/// Цена темы первого яруса в кредитах и её срок в секундах (`TIER_CREDITS`, `TIER_SECONDS`).
const TIER_CREDITS: f64 = 100.0;
const TIER_SECONDS: f64 = 20.0;

pub fn topic_spec(topic: &str) -> Option<&'static TopicSpec> {
  TOPICS.iter().find(|(name, _)| name == topic).map(|(_, spec)| spec)
}

pub fn is_topic(topic: &str) -> bool {
  topic_spec(topic).is_some()
}

/// Тема-открытие: её не исследуют, она открывается сама (`isTrigger`).
pub fn is_trigger(topic: &str) -> bool {
  topic_spec(topic).is_some_and(|spec| spec.trigger.is_some())
}

/// Предшественники темы: без них её не начать (`topicsBefore`).
pub fn topics_before(topic: &str) -> &'static [String] {
  topic_spec(topic).map_or(&[], |spec| &spec.after)
}

/// Ярус сложности: 1 — без предшественников, дальше — на единицу глубже самого глубокого; открытия сложности не
/// добавляют (`tierOf`).
pub fn tier_of(topic: &str) -> f64 {
  1.0 + topics_before(topic).iter().filter(|before| !is_trigger(before)).map(|before| tier_of(before)).fold(0.0, f64::max)
}

/// Цена темы целиком: кредиты, сырьё и секунды работы одной лаборатории при полной энергии (`TopicPrice`).
pub struct Price {
  pub cost: f64,
  pub materials: Vec<(String, f64)>,
  pub seconds: f64,
}

/// Что тема стоит по правилам мира (`topicPrice`).
pub fn topic_price(sim: &Sim, topic: &str) -> Price {
  if is_trigger(topic) {
    return Price { cost: 0.0, materials: Vec::new(), seconds: 0.0 };
  }
  let factor = js_pow(sim.rule("researchGrowth"), tier_of(topic) - 1.0);
  let scale = sim.rule("researchScale");
  let uses = topic_spec(topic).and_then(|spec| spec.uses.as_ref());
  let materials = uses.map_or_else(Vec::new, |uses| uses.iter().filter_map(|(good, amount)| amount.as_f64().filter(|&amount| amount > 0.0).map(|amount| (good.clone(), js_round(amount * factor * scale)))).collect());
  Price { cost: js_round(TIER_CREDITS * factor * scale), materials, seconds: TIER_SECONDS * factor * sim.rule("researchSlow") }
}

fn strings(world: &World, entity: kharos_engine::Entity, field: &str) -> Vec<String> {
  world.get(entity, "Player").and_then(|player| player.get(field)).and_then(Value::as_array).map_or_else(Vec::new, |list| list.iter().filter_map(|item| item.as_str().map(str::to_owned)).collect())
}

/// План игрока (`Player.plan`).
pub fn plan_of(world: &World, player: f64) -> Vec<String> {
  player_entity(world, player).map_or_else(Vec::new, |entity| strings(world, entity, "plan"))
}

/// Что игрок уже исследовал (`Player.research`).
pub fn research_of(world: &World, player: f64) -> Vec<String> {
  player_entity(world, player).map_or_else(Vec::new, |entity| strings(world, entity, "research"))
}

/// Темы плана, которые можно вести сейчас, в порядке плана; первая — головная (`openTopics`).
pub fn open_topics(world: &World, player: f64) -> Vec<String> {
  let Some(entity) = player_entity(world, player) else { return Vec::new() };
  let known = strings(world, entity, "research");
  strings(world, entity, "plan").into_iter().filter(|topic| is_topic(topic) && !known.contains(topic) && topics_before(topic).iter().all(|before| known.contains(before))).collect()
}

/// Тема, которую лаборатории игрока ведут сейчас: головная, а если та встала без сырья — выбранная взамен
/// (`currentTopic`).
pub fn current_topic(sim: &Sim, player: f64) -> Option<String> {
  let open = open_topics(&sim.world, player);
  match sim.research_picks.get(&(player as i64)) {
    Some(pick) if open.contains(pick) => Some(pick.clone()),
    _ => open.into_iter().next(),
  }
}

/// Сколько чего ещё потратят нынешняя тема и головная, если ведут не её (`researchNeeds`), в порядке появления.
pub fn research_needs(sim: &Sim, player: f64) -> Vec<(String, f64)> {
  let Some(current) = current_topic(sim, player) else { return Vec::new() };
  let Some(entity) = player_entity(&sim.world, player) else { return Vec::new() };
  let progress = sim.world.get(entity, "Player").and_then(|data| data.get("progress")).cloned().unwrap_or(Value::Null);
  let head = open_topics(&sim.world, player).into_iter().next();
  let mut topics = Vec::new();
  for topic in head.into_iter().chain([current]) {
    if !topics.contains(&topic) {
      topics.push(topic);
    }
  }
  let mut needs: Vec<(String, f64)> = Vec::new();
  for topic in topics {
    let left = 1.0 - progress.get(&topic).and_then(Value::as_f64).unwrap_or(0.0);
    for (good, amount) in topic_price(sim, &topic).materials {
      match needs.iter_mut().find(|(known, _)| *known == good) {
        Some((_, need)) => *need += amount * left,
        None => needs.push((good, amount * left)),
      }
    }
  }
  needs
}
