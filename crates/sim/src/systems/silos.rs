//! Ядерный удар (silos.ts, `launchMissiles`): шахты собирают боеголовки и пускают ракеты, перехватчики собирают свои
//! и стреляют, ракеты летят и рвутся — их взрыв бьёт в этот же бой — или гибнут от перехватчика; метки, чей срок
//! вышел, пропадают.

use crate::common::{alliances, friend, rect_distance};
use crate::components::make;
use crate::data::DATA;
use crate::economy::pay;
use crate::logistics::{network_stock, take_from_network};
use crate::math::{js_hypot, js_round};
use crate::spawning::spawn;
use crate::values::{js_number, number, text};
use crate::{Sim, Time};
use kharos_engine::{Entity, World};
use serde_json::{json, Map, Value};

/// Сколько секунд место пуска отмечено у игроков с радаром (`LAUNCH_MARK`).
const LAUNCH_MARK: f64 = 60.0;
/// Раз во сколько секунд сверяется, чьё под ударом (`WATCH`).
const WATCH: f64 = 1.0;
/// Сколько секунд вспышка взрыва открывает всем его круг (`GLARE`).
const GLARE: f64 = 5.0;
/// Высота полёта ракеты и за сколько секунд она её набирает (`CRUISE`, `CLIMB`).
const CRUISE: f64 = 2.5;
const CLIMB: f64 = 3.0;
/// Размер вспышки сбитой ракеты (`AIRBURST`).
const AIRBURST: f64 = 1.5;

fn field(spec: &Value, path: &[&str]) -> f64 {
  path.iter().try_fold(spec, |value, key| value.get(key)).and_then(Value::as_f64).unwrap_or(0.0)
}

/// Боеголовка ракетной шахты: вид шахты один (`WARHEAD`).
fn warhead() -> &'static Value {
  DATA.buildings["silo"].warhead.as_ref().expect("у шахты нет боеголовки")
}

/// На какой высоте ракета, которая летит age секунд и которой осталось left тайлов (`missileLift`).
fn missile_lift(age: f64, left: f64) -> f64 {
  CRUISE * 0f64.max(1f64.min(age / CLIMB).min(left / (field(warhead(), &["speed"]) * CLIMB)))
}

fn get(world: &World, entity: Entity, key: &str, name: &str) -> f64 {
  number(world, entity, key, name).unwrap_or(0.0)
}

fn set(world: &mut World, entity: Entity, key: &str, fields: &[(&str, Value)]) {
  world.set(entity, key, fields.iter().map(|(name, value)| ((*name).to_owned(), value.clone())).collect::<Map<String, Value>>());
}

fn size_of(world: &World, entity: Entity) -> (f64, f64) {
  text(world, entity, "Building", "type").map_or((0.0, 0.0), |kind| (DATA.buildings[kind].width, DATA.buildings[kind].height))
}

/// Игроки, у которых есть готовый радар (`radarOwners`).
fn radar_owners(world: &World) -> Vec<f64> {
  let mut players = Vec::new();
  for entity in world.query(&["Building", "Owner"]) {
    let scans = text(world, entity, "Building", "type").is_some_and(|kind| DATA.buildings[kind].scan.is_some_and(|scan| scan != 0.0));
    let player = get(world, entity, "Owner", "player");
    if scans && !world.has(entity, "Site") && !players.contains(&player) {
      players.push(player);
    }
  }
  players.retain(|&player| player != 0.0);
  players
}

/// Игроки, чьи здания или юниты не дальше radius от точки (`playersNear`).
fn players_near(world: &World, x: f64, y: f64, radius: f64) -> Vec<f64> {
  let mut players = Vec::new();
  for entity in world.query(&["Owner", "Position"]) {
    let player = get(world, entity, "Owner", "player");
    if player == 0.0 || players.contains(&player) {
      continue;
    }
    let (width, height) = size_of(world, entity);
    if rect_distance(get(world, entity, "Position", "x"), get(world, entity, "Position", "y"), width, height, x, y) <= radius {
      players.push(player);
    }
  }
  players
}

/// Игроки, чьё под ударом ракеты, кроме её хозяина.
fn under(world: &World, missile: Entity) -> Value {
  let player = get(world, missile, "Missile", "player");
  let near = players_near(world, get(world, missile, "Missile", "toX"), get(world, missile, "Missile", "toY"), field(warhead(), &["blast", "radius"]));
  json!(near.into_iter().filter(|&viewer| viewer != player).map(js_number).collect::<Vec<_>>())
}

/// Сколько тиков ещё лететь ракете (`flightLeft`).
fn flight_left(world: &World, missile: Entity, speed: f64, step: f64) -> f64 {
  (js_hypot(get(world, missile, "Missile", "toX") - get(world, missile, "Missile", "x"), get(world, missile, "Missile", "toY") - get(world, missile, "Missile", "y")) / (speed * step)).ceil()
}

/// Через сколько тиков ракета-перехватчик из (x, y) встретит ядерную ракету (`meetTicks`).
fn meet_ticks(world: &World, missile: Entity, x: f64, y: f64, speed: f64, step: f64) -> f64 {
  let flight = field(warhead(), &["speed"]) * step;
  let (from_x, from_y) = (get(world, missile, "Missile", "x"), get(world, missile, "Missile", "y"));
  let (to_x, to_y) = (get(world, missile, "Missile", "toX"), get(world, missile, "Missile", "toY"));
  let reach = js_hypot(to_x - from_x, to_y - from_y);
  let last = (reach / flight).ceil() - 1.0;
  let mut k = 1.0;
  while k < last {
    let flown = reach.min(flight * k) / if reach != 0.0 { reach } else { 1.0 };
    let (meet_x, meet_y) = (from_x + (to_x - from_x) * flown, from_y + (to_y - from_y) * flown);
    if js_hypot(meet_x - x, meet_y - y) <= speed * step * k {
      return k;
    }
    k += 1.0;
  }
  0f64.max(last)
}

/// Хватает ли в сети здания готового на эту цену (`networkHas`).
fn network_has(sim: &mut Sim, building: Entity, materials: &[(String, f64)]) -> bool {
  materials.iter().filter(|(_, amount)| *amount > 0.0).all(|(good, amount)| network_stock(sim, building, good) >= amount - 1e-6)
}

fn amounts(spec: &Value) -> Vec<(String, f64)> {
  spec.get("materials").and_then(Value::as_object).map_or_else(Vec::new, |materials| materials.iter().filter_map(|(good, amount)| amount.as_f64().map(|amount| (good.clone(), amount))).collect())
}

/// Перехватчики собирают ракеты и стреляют по чужим ядерным, которые метят в их круг (`intercept`).
fn intercept(sim: &mut Sim, time: Time) {
  struct Guard {
    entity: Entity,
    player: f64,
    x: f64,
    y: f64,
    radius: f64,
    speed: f64,
  }
  let mut armed = Vec::new();
  for entity in sim.world.query(&["Interceptor", "Building", "Position", "Owner"]) {
    let kind = text(&sim.world, entity, "Building", "type").unwrap_or_default();
    let building = &DATA.buildings[kind];
    let Some(spec) = building.intercept.as_ref() else { continue };
    if sim.world.has(entity, "Site") || sim.world.has(entity, "Off") {
      continue;
    }
    let player = get(&sim.world, entity, "Owner", "player");
    let ticks = get(&sim.world, entity, "Interceptor", "ticks");
    let missiles = get(&sim.world, entity, "Interceptor", "missiles");
    let capacity = field(spec, &["capacity"]);
    if ticks > 0.0 {
      let progress = get(&sim.world, entity, "Interceptor", "progress") + sim.power.get(&entity).copied().unwrap_or(0.0);
      if progress >= ticks {
        set(&mut sim.world, entity, "Interceptor", &[("progress", json!(0)), ("ticks", json!(0)), ("missiles", js_number(missiles + 1.0))]);
      } else {
        set(&mut sim.world, entity, "Interceptor", &[("progress", js_number(progress))]);
      }
    } else if missiles < capacity {
      // Сборка начинается, когда хватает кредитов и готового в сети: цена списывается сразу.
      let materials = amounts(spec);
      let credits = crate::common::player_entity(&sim.world, player).map_or(0.0, |account| get(&sim.world, account, "Player", "credits"));
      let cost = field(spec, &["cost"]);
      if credits >= cost && network_has(sim, entity, &materials) && pay(&mut sim.world, player, cost) {
        take_from_network(sim, entity, &materials);
        set(&mut sim.world, entity, "Interceptor", &[("progress", json!(0)), ("ticks", js_number(js_round(field(spec, &["seconds"]) / time.step)))]);
      }
    }
    if get(&sim.world, entity, "Interceptor", "missiles") > 0.0 {
      let (x, y) = (get(&sim.world, entity, "Position", "x"), get(&sim.world, entity, "Position", "y"));
      armed.push(Guard { entity, player, x: x + building.width / 2.0, y: y + building.height / 2.0, radius: field(spec, &["radius"]), speed: field(spec, &["speed"]) });
    }
  }
  if armed.is_empty() {
    return;
  }
  let friends = alliances(&sim.world);
  let world = &mut sim.world;
  for entity in world.query(&["Missile"]) {
    if get(world, entity, "Missile", "hunted") >= 0.0 {
      continue;
    }
    let player = get(world, entity, "Missile", "player");
    let (x, y) = (get(world, entity, "Missile", "x"), get(world, entity, "Missile", "y"));
    let (to_x, to_y) = (get(world, entity, "Missile", "toX"), get(world, entity, "Missile", "toY"));
    let mut best: Option<&Guard> = None;
    let mut best_reach = f64::INFINITY;
    for guard in &armed {
      if guard.player == player || friend(&friends, guard.player, player) {
        continue;
      }
      if get(world, guard.entity, "Interceptor", "missiles") == 0.0 {
        continue;
      }
      let reach = js_hypot(x - guard.x, y - guard.y);
      if reach > guard.radius || reach >= best_reach || js_hypot(to_x - guard.x, to_y - guard.y) > guard.radius {
        continue;
      }
      best = Some(guard);
      best_reach = reach;
    }
    let Some(best) = best else { continue };
    let left = get(world, best.entity, "Interceptor", "missiles") - 1.0;
    set(world, best.entity, "Interceptor", &[("missiles", js_number(left))]);
    let k = meet_ticks(world, entity, best.x, best.y, best.speed, time.step);
    set(world, entity, "Missile", &[("hunted", js_number(time.tick as f64 + k))]);
    if k == 0.0 {
      continue;
    }
    let reach = js_hypot(to_x - x, to_y - y);
    let reach = if reach != 0.0 { reach } else { 1.0 };
    let ahead = reach.min(field(warhead(), &["speed"]) * time.step * k);
    let born = get(world, entity, "Missile", "born");
    let chaser = make(
      "Chaser",
      [
        ("fromX", js_number(best.x)),
        ("fromY", js_number(best.y)),
        ("toX", js_number(x + (to_x - x) * ahead / reach)),
        ("toY", js_number(y + (to_y - y) * ahead / reach)),
        ("lift", js_number(missile_lift((time.tick as f64 + k - born) * time.step, reach - ahead))),
        ("born", js_number(time.tick as f64)),
        ("arrive", js_number(time.tick as f64 + k)),
        ("player", js_number(best.player)),
        ("missile", json!(entity)),
      ],
    );
    spawn(world, vec![("Chaser", chaser)]);
  }
}

fn position(x: f64, y: f64) -> Value {
  make("Position", [("x", js_number(x)), ("y", js_number(y))])
}

/// Раз в тик, до боя: см. описание модуля.
pub fn launch_missiles(sim: &mut Sim, time: Time) {
  let tick = time.tick as f64;
  let mut fired = Vec::new();
  for entity in sim.world.query(&["Silo", "Building", "Position", "Owner"]) {
    let world = &mut sim.world;
    let ticks = get(world, entity, "Silo", "ticks");
    let ready = world.get(entity, "Silo").and_then(|silo| silo.get("ready")).and_then(Value::as_bool).unwrap_or(false);
    if ticks > 0.0 && !ready && !world.has(entity, "Off") {
      // Энергии не хватает — сборка идёт медленнее.
      let progress = get(world, entity, "Silo", "progress") + sim.power.get(&entity).copied().unwrap_or(0.0);
      if progress >= ticks {
        set(world, entity, "Silo", &[("progress", json!(0)), ("ticks", json!(0)), ("cost", json!(0)), ("ready", json!(true))]);
      } else {
        set(world, entity, "Silo", &[("progress", js_number(progress))]);
      }
    }
    let kind = text(world, entity, "Building", "type").unwrap_or_default();
    let building = &DATA.buildings[kind];
    let Some(spec) = building.warhead.as_ref() else { continue };
    let start = get(world, entity, "Silo", "start");
    if start < 0.0 || tick - start < js_round(field(spec, &["launch"]) / time.step) {
      continue;
    }
    let (x, y) = (get(world, entity, "Position", "x"), get(world, entity, "Position", "y"));
    fired.push((entity, get(world, entity, "Owner", "player"), x + building.width / 2.0, y + building.height / 2.0, get(world, entity, "Silo", "toX"), get(world, entity, "Silo", "toY")));
  }
  intercept(sim, time);
  let world = &mut sim.world;
  let speed = field(warhead(), &["speed"]);
  let mut arrived = Vec::new();
  let mut downed = Vec::new();
  for entity in world.query(&["Missile"]) {
    let hunted = get(world, entity, "Missile", "hunted");
    if hunted >= 0.0 && tick >= hunted {
      downed.push(entity);
      continue;
    }
    let (x, y) = (get(world, entity, "Missile", "x"), get(world, entity, "Missile", "y"));
    let (to_x, to_y) = (get(world, entity, "Missile", "toX"), get(world, entity, "Missile", "toY"));
    let reach = js_hypot(to_x - x, to_y - y);
    let step = speed * time.step;
    if reach <= step {
      set(world, entity, "Missile", &[("x", js_number(to_x)), ("y", js_number(to_y))]);
      arrived.push(entity);
      continue;
    }
    set(world, entity, "Missile", &[("x", js_number(x + (to_x - x) / reach * step)), ("y", js_number(y + (to_y - y) / reach * step))]);
    // Под удар за время полёта входят и из-под него уходят.
    let mark = get(world, entity, "Missile", "mark");
    if (tick - get(world, entity, "Missile", "born")) % js_round(WATCH / time.step) == 0.0 && mark >= 0.0 && world.has(mark as Entity, "Mark") {
      let viewers = under(world, entity);
      set(world, mark as Entity, "Mark", &[("viewers", viewers)]);
    }
  }
  // Сбитая гибнет в воздухе: вспышка без урона, метка точки удара пропадает.
  for entity in downed {
    let mark = get(world, entity, "Missile", "mark");
    if mark >= 0.0 && world.has(mark as Entity, "Mark") {
      world.destroy(mark as Entity);
    }
    let (x, y) = (get(world, entity, "Missile", "x"), get(world, entity, "Missile", "y"));
    let lift = missile_lift((tick - get(world, entity, "Missile", "born")) * time.step, js_hypot(get(world, entity, "Missile", "toX") - x, get(world, entity, "Missile", "toY") - y));
    spawn(world, vec![("Position", position(x, y)), ("Blast", make("Blast", [("size", js_number(AIRBURST)), ("lift", js_number(lift)), ("life", js_number(js_round((0.25 + AIRBURST * 0.2) / time.step)))]))]);
    world.destroy(entity);
  }
  for entity in arrived {
    let mark = get(world, entity, "Missile", "mark");
    if mark >= 0.0 && world.has(mark as Entity, "Mark") {
      world.destroy(mark as Entity);
    }
    let (x, y) = (get(world, entity, "Missile", "toX"), get(world, entity, "Missile", "toY"));
    let blast = |name: &str| js_number(field(warhead(), &["blast", name]));
    spawn(world, vec![("Position", position(x, y)), ("Burst", make("Burst", [("radius", blast("radius")), ("damage", blast("damage")), ("core", blast("core"))]))]);
    spawn(world, vec![("Position", position(x, y)), ("Glare", make("Glare", [("radius", blast("radius")), ("until", js_number(tick + js_round(GLARE / time.step)))]))]);
    world.destroy(entity);
  }

  // Стартовавшая ракета трогается со следующего тика: в тик born она над шахтой.
  for (silo, player, x, y, to_x, to_y) in fired {
    set(world, silo, "Silo", &[("ready", json!(false)), ("start", json!(-1))]);
    let missile = make("Missile", [("fromX", js_number(x)), ("fromY", js_number(y)), ("toX", js_number(to_x)), ("toY", js_number(to_y)), ("born", js_number(tick)), ("x", js_number(x)), ("y", js_number(y)), ("player", js_number(player))]);
    let missile = spawn(world, vec![("Missile", missile)]);
    let until = tick + flight_left(world, missile, speed, time.step) + 1.0;
    let viewers = under(world, missile);
    let mark = spawn(world, vec![("Position", position(to_x, to_y)), ("Mark", make("Mark", [("kind", json!("strike")), ("until", js_number(until)), ("player", js_number(player)), ("viewers", viewers)]))]);
    set(world, missile, "Missile", &[("mark", json!(mark))]);
    let viewers = json!(radar_owners(world).into_iter().filter(|&viewer| viewer != player).map(js_number).collect::<Vec<_>>());
    spawn(world, vec![("Position", position(x, y)), ("Mark", make("Mark", [("kind", json!("launch")), ("until", js_number(tick + js_round(LAUNCH_MARK / time.step))), ("player", js_number(player)), ("viewers", viewers)]))]);
  }

  let mut expired = Vec::new();
  for entity in world.query(&["Mark"]) {
    if tick >= get(world, entity, "Mark", "until") {
      expired.push(entity);
    }
  }
  for entity in world.query(&["Glare"]) {
    if tick >= get(world, entity, "Glare", "until") {
      expired.push(entity);
    }
  }
  for entity in world.query(&["Chaser"]) {
    if tick >= get(world, entity, "Chaser", "arrive") {
      expired.push(entity);
    }
  }
  for entity in expired {
    world.destroy(entity);
  }
}
