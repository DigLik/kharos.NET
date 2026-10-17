//! Ход юнитов (movement.ts): к точке пути с объездом соседей, поворот со своей скоростью, скольжение вдоль стен,
//! расталкивание; дошедшие и застрявшие ищут путь заново. Точная копия TS: тот же порядок, та же арифметика.

use crate::data::DATA;
use crate::math::{js_atan2, js_cos, js_hypot, js_sin, turn_toward, wrap};
use crate::path::{is_clear, Grid, SEARCH_LIMIT};
use crate::values::{js_number, number, patch, text};
use crate::vision::allies_in;
use crate::walk::{fastest_of, flies, on_foot, order_move, Ground, Moved, JAM_TRIES};
use crate::{Sim, Time};
use kharos_engine::Entity;
use serde_json::{Map, Value};
use std::collections::{HashMap, HashSet};

const WAIT_TICKS: f64 = 30.0;
const SLIDE_TICKS: f64 = 4.0;
const PROGRESS: f64 = 0.05;
const MAX_TRIES: f64 = JAM_TRIES;
const GIVE_UP_REACH: f64 = 4.0;
const REACHED: f64 = 0.6;
const SETTLE: f64 = 1.0;
const ORBIT_MARGIN: f64 = 1.3;
const HORIZON: f64 = 1.2;
const AIMED: f64 = 0.05;
const GAP: f64 = 0.1;
const AVOIDANCE: f64 = 2.0;
const SEPARATIONS: usize = 2;
const LOST_SEARCHES: usize = 8;
const LOST_TILES: f64 = 8000.0;
const ROAD_LOOK_TICKS: u64 = 20;
const SENSE: f64 = 2.5;
const CELL: f64 = 2.0;
const DRIFT: f64 = 1.0;
const LARGE: f64 = 0.9;
/// Сколько тайлов за тик осматривают поиски пути упёршихся юнитов, ехавших напрямую.
const PLAN_TILES: f64 = 20000.0;

struct Body {
  entity: Entity,
  x: f64,
  y: f64,
  radius: f64,
  mass: f64,
  vx: f64,
  vy: f64,
  air: bool,
  foot: bool,
  moving: bool,
  rooted: bool,
  owner: f64,
}

/// Путь юнита, как он лежит в Path: меняется на месте и пишется обратно в конце.
struct Route {
  points: Vec<f64>,
  goal_x: f64,
  goal_y: f64,
  wait: f64,
  tries: f64,
  near: f64,
  roads: bool,
  direct: bool,
  stuck: bool,
  best: f64,
}

impl Route {
  fn read(world: &kharos_engine::World, entity: Entity) -> Route {
    let path = world.get(entity, "Path");
    let field = |name| path.and_then(|path| path.get(name)).and_then(Value::as_f64).unwrap_or(0.0);
    let flag = |name| path.and_then(|path| path.get(name)).and_then(Value::as_bool).unwrap_or(false);
    let points = path.and_then(|path| path.get("points")).and_then(Value::as_array).map(|list| list.iter().filter_map(Value::as_f64).collect()).unwrap_or_default();
    Route { points, goal_x: field("goalX"), goal_y: field("goalY"), wait: field("wait"), tries: field("tries"), near: field("near"), roads: flag("roads"), direct: flag("direct"), stuck: flag("stuck"), best: field("best") }
  }

  fn patch(&self) -> Map<String, Value> {
    let mut fields = patch([("wait", self.wait), ("tries", self.tries), ("best", self.best)]);
    fields.insert("points".to_owned(), Value::Array(self.points.iter().map(|&value| js_number(value)).collect()));
    fields.insert("stuck".to_owned(), Value::Bool(self.stuck));
    fields
  }
}

struct Mover {
  index: usize,
  route: Route,
  walled: bool,
  slid: bool,
}

/// Тела по ячейкам: соседей ищут по ним.
struct Crowd {
  bodies: Vec<Body>,
  cells: HashMap<(i32, i32), Vec<usize>>,
  large: Vec<usize>,
}

impl Crowd {
  /// Соседи тела, чьи края ближе reach тайлов к его краю (`gather`).
  fn gather(&self, me: usize, reach: f64) -> Vec<usize> {
    let mut found = Vec::new();
    let body = &self.bodies[me];
    let (x, y) = (body.x, body.y);
    let check = |found: &mut Vec<usize>, other: usize| {
      let them = &self.bodies[other];
      if other == me || them.air != body.air {
        return;
      }
      let far = body.radius + them.radius + reach;
      let (offset_x, offset_y) = (them.x - x, them.y - y);
      if offset_x * offset_x + offset_y * offset_y < far * far {
        found.push(other);
      }
    };
    let far = body.radius + LARGE + reach + DRIFT;
    let (right, bottom) = (((x + far) / CELL).floor() as i32, ((y + far) / CELL).floor() as i32);
    for cy in ((y - far) / CELL).floor() as i32..=bottom {
      for cx in ((x - far) / CELL).floor() as i32..=right {
        for &other in self.cells.get(&(cx, cy)).map_or(&[][..], Vec::as_slice) {
          check(&mut found, other);
        }
      }
    }
    for &other in &self.large {
      check(&mut found, other);
    }
    found
  }

  /// Куда ехать, чтобы не столкнуться (`steer`).
  fn steer(&self, me: usize, dir_x: f64, dir_y: f64, speed: f64) -> f64 {
    let body = &self.bodies[me];
    let (mut away_x, mut away_y) = (0.0, 0.0);
    let (own_x, own_y) = (dir_x * speed, dir_y * speed);
    for other in self.gather(me, SENSE) {
      let them = &self.bodies[other];
      let (offset_x, offset_y) = (them.x - body.x, them.y - body.y);
      let (relative_x, relative_y) = (own_x - them.vx, own_y - them.vy);
      let closing = relative_x * relative_x + relative_y * relative_y;
      let reach = body.radius + them.radius + GAP;
      let when = if closing > 1e-6 { (offset_x * relative_x + offset_y * relative_y) / closing } else { 0.0 };
      if when <= 0.0 || when > HORIZON {
        continue;
      }
      let (miss_x, miss_y) = (offset_x - relative_x * when, offset_y - relative_y * when);
      let miss = js_hypot(miss_x, miss_y);
      if miss >= reach {
        continue;
      }
      let length = closing.sqrt();
      let side_x = if miss > 1e-3 { -miss_x / miss } else { -relative_y / length };
      let side_y = if miss > 1e-3 { -miss_y / miss } else { relative_x / length };
      let urgency = (1.0 - when / HORIZON) * ((reach - miss) / reach);
      away_x += side_x * urgency;
      away_y += side_y * urgency;
    }
    js_atan2(dir_y + away_y * AVOIDANCE, dir_x + away_x * AVOIDANCE)
  }
}

/// Сетка «где может стоять это тело» без цены шага: для проверки прямой мимо занятой точки.
struct Stand<'g, 'a> {
  ground: &'g mut Ground<'a>,
  air: bool,
  foot: bool,
}

impl Grid for Stand<'_, '_> {
  fn walkable(&mut self, x: i32, y: i32) -> bool {
    self.ground.can_stand(self.air, x, y, self.foot)
  }
}

struct Further {
  entity: Entity,
  x: i32,
  y: i32,
  near: f64,
  blocked: Option<(i32, i32)>,
}

struct Lost {
  entity: Entity,
  index: usize,
  x: i32,
  y: i32,
  tries: f64,
  near: f64,
}

/// Раз в тик: двигает юниты по их путям (`moveUnits`).
pub fn move_units(sim: &mut Sim, time: Time) {
  let step = time.step;
  let world = &sim.world;
  let mut crowd = Crowd { bodies: Vec::new(), cells: HashMap::new(), large: Vec::new() };
  let mut index = HashMap::new();
  let mut facings = Vec::new();
  for entity in world.query(&["Position", "Unit"]) {
    let kind = text(world, entity, "Unit", "type").unwrap_or_default();
    let spec = DATA.units.get(kind);
    let (radius, speed) = spec.map_or((0.0, 0.0), |spec| (spec.radius, spec.speed));
    let (x, y) = (number(world, entity, "Position", "x").unwrap_or(0.0), number(world, entity, "Position", "y").unwrap_or(0.0));
    let unit = |name| number(world, entity, "Unit", name).unwrap_or(0.0);
    let (mut vx, mut vy) = ((x - unit("prevX")) / step, (y - unit("prevY")) / step);
    if js_hypot(vx, vy) > speed * 1.5 {
      (vx, vy) = (0.0, 0.0);
    }
    let owner = number(world, entity, "Owner", "player").unwrap_or(0.0);
    let body = Body { entity, x, y, radius, mass: radius * radius, vx, vy, air: flies(kind), foot: on_foot(kind), moving: world.has(entity, "Path"), rooted: world.has(entity, "Converting"), owner };
    index.insert(entity, crowd.bodies.len());
    // Клиент рисует юнит между прошлым и нынешним положением: прошлое — у всех, даже у стоящих.
    facings.push((unit("facing"), (x, y)));
    if radius > LARGE {
      crowd.large.push(crowd.bodies.len());
    } else {
      crowd.cells.entry(((x / CELL).floor() as i32, (y / CELL).floor() as i32)).or_default().push(crowd.bodies.len());
    }
    crowd.bodies.push(body);
  }
  let prev_facings: Vec<f64> = facings.iter().map(|(facing, _)| *facing).collect();
  let mut facing_now = prev_facings.clone();
  let mut allies: HashMap<u64, Vec<f64>> = HashMap::new();
  let mut allied = |a: f64, b: f64| a != b && allies.entry(a as u64).or_insert_with(|| allies_in(world, a as u32)).contains(&b);
  let yields = |crowd: &Crowd, body: usize, by: usize, allied: &mut dyn FnMut(f64, f64) -> bool| {
    let (them, by) = (&crowd.bodies[body], &crowd.bodies[by]);
    !them.rooted && (them.moving || them.owner == by.owner || allied(them.owner, by.owner))
  };

  let mut ground = Ground::new(&mut sim.land, &sim.layers, world, &sim.occupancy, sim.vision.bounds, &sim.rules);
  let mut movers: Vec<Mover> = Vec::new();
  let mut roadward: Vec<(Entity, i32, i32, f64)> = Vec::new();
  for entity in world.query(&["Position", "Unit", "Path"]) {
    let me = index[&entity];
    let kind = text(world, entity, "Unit", "type").unwrap_or_default();
    let Some(spec) = DATA.units.get(kind) else { continue };
    let (speed, turn, radius) = (spec.speed, spec.turn, spec.radius);
    let route = Route::read(world, entity);
    if !route.roads && fastest_of(kind) > 1.0 && (time.tick + entity as u64) % ROAD_LOOK_TICKS == 0 && ground.road_in_sight(entity) {
      roadward.push((entity, route.goal_x as i32, route.goal_y as i32, route.near));
    }
    let (body_air, body_foot) = (crowd.bodies[me].air, crowd.bodies[me].foot);
    let (x, y) = (crowd.bodies[me].x, crowd.bodies[me].y);
    // Юнит, под которым выросло здание, выходит из него: внутри здания тайлы ему не преграда.
    let inside = !ground.can_stand(body_air, x.floor() as i32, y.floor() as i32, body_foot);
    let rooted = crowd.bodies[me].rooted;
    movers.push(Mover { index: me, route, walled: false, slid: false });
    if rooted {
      continue;
    }
    let mover = movers.last_mut().unwrap();
    let points = &mover.route.points;
    let open = |ground: &mut Ground, x: f64, y: f64| inside || ground.can_stand(body_air, x.floor() as i32, y.floor() as i32, body_foot);
    let (dx, dy) = (points[0] - x, points[1] - y);
    let distance = js_hypot(dx, dy);
    let facing = facing_now[me];
    let wanted = if distance != 0.0 { js_atan2(dy, dx) } else { facing };
    let last = points.len() == 2;
    let heading = if distance != 0.0 && !(last && distance < radius + SETTLE) { crowd.steer(me, dx / distance, dy / distance, speed) } else { wanted };
    let facing = turn_toward(facing, heading, turn * step);
    facing_now[me] = facing;
    let askew = wrap(heading - facing).abs();
    let mut moved = speed * ground.terrain_speed(kind, x.floor() as i32, y.floor() as i32) * step * 0f64.max(js_cos(askew));
    let off = wrap(wanted - facing).abs();
    let avoiding = wrap(heading - wanted).abs() > AIMED;
    if !avoiding && distance < ((2.0 * speed) / turn) * js_sin(off.min(std::f64::consts::PI / 2.0)) * ORBIT_MARGIN {
      moved = 0.0;
    }
    if moved == 0.0 {
      continue;
    }
    // Глядя точно на точку пути, юнит приходит в неё ровно, без перелёта.
    if facing == wanted && moved >= distance {
      if open(&mut ground, points[0], points[1]) {
        (crowd.bodies[me].x, crowd.bodies[me].y) = (points[0], points[1]);
      } else {
        mover.walled = true;
      }
      continue;
    }
    let (step_x, step_y) = (js_cos(facing) * moved, js_sin(facing) * moved);
    if open(&mut ground, x + step_x, y + step_y) {
      (crowd.bodies[me].x, crowd.bodies[me].y) = (x + step_x, y + step_y);
      continue;
    }
    // Впереди непроходимое — скользит вдоль него: по той оси, по которой можно и которая ближе к точке.
    let along_x = if open(&mut ground, x + step_x, y) { step_x * dx } else { f64::NEG_INFINITY };
    let along_y = if open(&mut ground, x, y + step_y) { step_y * dy } else { f64::NEG_INFINITY };
    if along_x > 0.0 && along_x >= along_y {
      crowd.bodies[me].x = x + step_x;
    } else if along_y > 0.0 {
      crowd.bodies[me].y = y + step_y;
    } else {
      mover.walled = true;
    }
    mover.slid = true;
  }

  // Расталкивание: начинают те, кто ехал, — стоящие, которых сдвинули, продолжают цепочку.
  let mut pushed: Vec<usize> = movers.iter().map(|mover| mover.index).collect();
  let mut in_pushed: HashSet<usize> = pushed.iter().copied().collect();
  let mut pass = 0;
  let mut moved = true;
  while pass < SEPARATIONS && moved {
    moved = false;
    for body in pushed.clone() {
      for other in crowd.gather(body, 0.0) {
        let (bx, by) = (crowd.bodies[body].x, crowd.bodies[body].y);
        let (ox, oy) = (crowd.bodies[other].x, crowd.bodies[other].y);
        let (mut normal_x, mut normal_y) = (ox - bx, oy - by);
        let distance = js_hypot(normal_x, normal_y);
        let overlap = crowd.bodies[body].radius + crowd.bodies[other].radius - distance;
        if overlap <= 0.0 {
          continue;
        }
        if distance > 1e-6 {
          normal_x /= distance;
          normal_y /= distance;
        } else {
          // Стоят точка в точку: расходятся в сторону, заданную номерами, — одинаково при каждом прогоне.
          let angle = ((crowd.bodies[body].entity as u64 * 7 + crowd.bodies[other].entity as u64 * 13) % 360) as f64;
          normal_x = js_cos(angle);
          normal_y = js_sin(angle);
        }
        let body_free = yields(&crowd, body, other, &mut allied);
        let other_free = yields(&crowd, other, body, &mut allied);
        let (body_mass, other_mass) = (crowd.bodies[body].mass, crowd.bodies[other].mass);
        let mut body_share = if !body_free {
          0.0
        } else if !other_free {
          1.0
        } else {
          other_mass / (body_mass + other_mass)
        };
        if body_free && other_free && crowd.bodies[body].moving != crowd.bodies[other].moving {
          body_share = if crowd.bodies[body].moving { 0.0 } else { 1.0 };
        }
        let mut other_share = if other_free { 1.0 - body_share } else { 0.0 };
        let other_x = ox + normal_x * overlap * other_share;
        let other_y = oy + normal_y * overlap * other_share;
        let (other_air, other_foot) = (crowd.bodies[other].air, crowd.bodies[other].foot);
        if other_share != 0.0 && !ground.can_stand(other_air, other_x.floor() as i32, other_y.floor() as i32, other_foot) {
          other_share = 0.0;
          body_share = if body_free { 1.0 } else { 0.0 };
        }
        let body_x = bx - normal_x * overlap * body_share;
        let body_y = by - normal_y * overlap * body_share;
        let (body_air, body_foot) = (crowd.bodies[body].air, crowd.bodies[body].foot);
        if body_share != 0.0 && ground.can_stand(body_air, body_x.floor() as i32, body_y.floor() as i32, body_foot) {
          (crowd.bodies[body].x, crowd.bodies[body].y) = (body_x, body_y);
          moved = true;
        }
        if other_share != 0.0 {
          moved = true;
          (crowd.bodies[other].x, crowd.bodies[other].y) = (other_x, other_y);
          if in_pushed.insert(other) {
            pushed.push(other);
          }
        }
      }
    }
    pass += 1;
  }

  let mut stopped: Vec<Entity> = Vec::new();
  let mut lost: Vec<Lost> = Vec::new();
  let mut further: Vec<Further> = Vec::new();
  for mover in &mut movers {
    let me = mover.index;
    let (entity, x, y, radius, air, foot) = {
      let body = &crowd.bodies[me];
      (body.entity, body.x, body.y, body.radius, body.air, body.foot)
    };
    let route = &mut mover.route;
    let left = js_hypot(route.points[0] - x, route.points[1] - y);
    let last = route.points.len() == 2;
    let blocker = if left < radius + LARGE + SETTLE {
      let (px, py) = (route.points[0], route.points[1]);
      crowd.gather(me, SENSE).into_iter().find(|&other| js_hypot(crowd.bodies[other].x - px, crowd.bodies[other].y - py) < radius + crowd.bodies[other].radius)
    } else {
      None
    };
    // Промежуточную точку занял сосед, а юнит уже рядом: точка пропускается, если следующая видна по прямой.
    if let Some(blocker) = blocker {
      if !last && left < radius + crowd.bodies[blocker].radius + SETTLE {
        let clear = air || is_clear(&mut Stand { ground: &mut ground, air, foot }, x, y, route.points[2], route.points[3]);
        if clear {
          route.points.drain(0..2);
          route.best = -1.0;
          route.wait = 0.0;
          continue;
        }
      }
    }
    let settled = blocker.is_some_and(|blocker| {
      let them = &crowd.bodies[blocker];
      last && left < radius + them.radius + SETTLE && (!yields(&crowd, blocker, me, &mut allied) || (them.moving && them.entity < entity))
    });
    if left == 0.0 || settled || (!last && left < REACHED) {
      route.points.drain(0..2);
      route.tries = 0.0;
      route.best = -1.0;
      route.wait = 0.0;
      if !route.points.is_empty() {
        continue;
      }
      // Путь к далёкой цели кончается раньше неё: тогда это не приезд, и юнит ищет путь дальше отсюда.
      let (gx, gy) = (x - route.goal_x - 0.5, y - route.goal_y - 0.5);
      let reached = if route.near != 0.0 {
        gx * gx + gy * gy <= (route.near + 1.0) * (route.near + 1.0)
      } else {
        (x.floor() == route.goal_x && y.floor() == route.goal_y) || (settled && js_hypot(gx, gy) < radius + SETTLE + 1.0)
      };
      if reached || air {
        stopped.push(entity);
      } else {
        route.points.extend([x, y]);
        let blocked = if settled { blocker.map(|other| (crowd.bodies[other].x.floor() as i32, crowd.bodies[other].y.floor() as i32)) } else { None };
        further.push(Further { entity, x: route.goal_x as i32, y: route.goal_y as i32, near: route.near, blocked });
      }
      continue;
    }
    // Ехал напрямую и упёрся в местность или здание: путь нужен сразу.
    if route.direct && (mover.slid || mover.walled) {
      route.wait += 1.0;
      if route.wait >= SLIDE_TICKS {
        route.stuck = true;
        route.wait = 0.0;
        route.best = -1.0;
      }
      continue;
    }
    if route.best < 0.0 || left < route.best - PROGRESS {
      route.best = left;
      route.wait = 0.0;
      continue;
    }
    route.wait += 1.0;
    if route.wait >= WAIT_TICKS {
      // Юнит не может идти дальше: прокладывает путь заново или, если уже пробовал, встаёт (`giveUp`).
      let close = js_hypot(x - route.goal_x - 0.5, y - route.goal_y - 0.5) <= route.near + GIVE_UP_REACH;
      if route.direct {
        route.wait = 0.0;
        if mover.walled || {
          route.tries += 1.0;
          route.tries >= MAX_TRIES
        } {
          route.stuck = true;
        }
      } else if route.tries >= MAX_TRIES && close {
        stopped.push(entity);
      } else if lost.len() < LOST_SEARCHES {
        lost.push(Lost { entity, index: me, x: route.goal_x as i32, y: route.goal_y as i32, tries: (route.tries + 1.0).min(MAX_TRIES), near: route.near });
      }
      route.wait = 0.0;
      route.best = -1.0;
    }
  }
  drop(ground);

  // Запись: места, повороты, пути — всё, что TS меняет на месте.
  for (at, body) in crowd.bodies.iter().enumerate() {
    let (prev_x, prev_y) = facings[at].1;
    sim.world.set(body.entity, "Unit", patch([("prevX", prev_x), ("prevY", prev_y), ("prevFacing", prev_facings[at]), ("facing", facing_now[at])]));
    sim.world.set(body.entity, "Position", patch([("x", body.x), ("y", body.y)]));
  }
  for mover in &movers {
    sim.world.set(crowd.bodies[mover.index].entity, "Path", mover.route.patch());
  }
  for entity in stopped {
    sim.world.remove(entity, "Path");
  }

  let searched_before = sim.search.searched();
  let previous = sim.search.open_quota(LOST_TILES + SEARCH_LIMIT);
  let over = |sim: &Sim| sim.search.searched() - searched_before >= LOST_TILES;
  for Further { entity, x, y, near, blocked } in further {
    if over(sim) {
      continue;
    }
    let (Some(px), Some(py)) = (number(&sim.world, entity, "Position", "x"), number(&sim.world, entity, "Position", "y")) else { continue };
    let from = (px.floor() as i32, py.floor() as i32);
    order_move(sim, entity, x, y, None, 0.0, near, None);
    // Ближе к цели не подойти — встаёт: иначе искал бы на месте каждый тик.
    let end = Route::read(&sim.world, entity).points;
    if sim.world.has(entity, "Path") && end.len() >= 2 {
      let end = (end[end.len() - 2].floor() as i32, end[end.len() - 1].floor() as i32);
      if end == from || Some(end) == blocked {
        sim.world.remove(entity, "Path");
      }
    }
  }
  for (entity, x, y, near) in roadward {
    if over(sim) {
      break;
    }
    if !sim.world.has(entity, "Path") {
      continue;
    }
    order_move(sim, entity, x, y, None, 0.0, near, None);
  }
  for Lost { entity, index: me, x, y, tries, near } in lost {
    if over(sim) {
      break;
    }
    let result = order_move(sim, entity, x, y, None, tries, near, None);
    if !sim.world.has(entity, "Path") || result == Moved::Set || sim.search.cut() {
      continue;
    }
    // Нового пути нет — обступили со всех сторон. Обступили стоящие без приказа — значит, доехал.
    let neighbours = crowd.gather(me, GAP);
    if tries >= MAX_TRIES && !neighbours.is_empty() && neighbours.iter().all(|&other| !crowd.bodies[other].moving) {
      sim.world.remove(entity, "Path");
    }
  }
  sim.search.close_quota(previous);
}

/// Раз в тик, до движения: пути упёршимся, ехавшим напрямую (`planPaths`).
pub fn plan_paths(sim: &mut Sim, _time: Time) {
  let waiting: Vec<Entity> = sim.world.query(&["Path"]).into_iter().filter(|&entity| {
    let flag = |name| sim.world.get(entity, "Path").and_then(|path| path.get(name)).and_then(Value::as_bool) == Some(true);
    flag("direct") && flag("stuck")
  }).collect();
  let searched_before = sim.search.searched();
  let previous = sim.search.open_quota(PLAN_TILES);
  for entity in waiting {
    if sim.world.get(entity, "Path").and_then(|path| path.get("stuck")).and_then(Value::as_bool) != Some(true) {
      continue;
    }
    let route = Route::read(&sim.world, entity);
    order_move(sim, entity, route.goal_x as i32, route.goal_y as i32, None, 0.0, route.near, None);
    if sim.search.searched() - searched_before >= PLAN_TILES {
      break;
    }
  }
  sim.search.close_quota(previous);
}
