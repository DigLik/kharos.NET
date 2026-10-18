//! Подача из модов (docs/mods.md, «Клиент»): клиентская машина Luau и что она видит.
//!
//! Каждый кадр: сигналы хоста → `signals` видов; камера партии; панели (`ui/`); управление (`controls/`); атмосфера
//! (`atmosphere/`); музыка (`music/`). Скриптам дан `c`:
//!
//! - `c.input` — ввод кадра, `c.camera` — камера, `c.shared` — общая память скриптов клиента, `c.atmosphere` — свет,
//!   ветер и огни сцены, `c.music` — настроение и громкость музыки;
//! - мир на чтение, сглаженный между тиками: `c:each`, `c:get`, `c:proto`, `c:pick`, `c:within`;
//! - `c:send(приказ, аргументы)` — хосту; `c:sound(голос, …)`, `c:particle(…)`;
//! - `c.draw` — рисование поверх мира в тайлах, `c.ui` — интерфейс в пикселях экрана.

use crate::atlas::Look;
use crate::input::{Input, BUTTONS};
use crate::render::View;
use crate::ui::{id, Paint, Ui};
use kharos_world::host::State;
use kharos_world::Entity;
use mlua::{Lua, LuaSerdeExt, Table, UserData, UserDataFields, UserDataMethods, Value};
use serde_json::Value as Json;
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

/// Частица: место и скорость в тайлах, сколько живёт, цвет и размер в текселях.
#[derive(Clone, Copy, Debug)]
pub struct Particle {
  pub x: f32,
  pub y: f32,
  pub vx: f32,
  pub vy: f32,
  pub age: f32,
  pub life: f32,
  pub color: u32,
  pub size: f32,
  /// Сносит ли ветром.
  pub drift: bool,
}

/// Звук, заказанный скриптом за кадр.
pub struct SoundOrder {
  pub voice: String,
  pub at: Option<[f32; 2]>,
  pub gain: f32,
  pub pitch: f64,
  pub bus: String,
}

/// Значок вида для скриптов: кадр его облика в атласе.
pub struct LookInfo {
  pub uv: [f32; 4],
}

/// Состояние клиента, общее у Rust и `c`.
pub struct ClientState {
  pub world: Rc<RefCell<State>>,
  /// Место и направление сущностей тиком раньше — для сглаживания; alpha — доля тика с последнего.
  pub prev: HashMap<Entity, [f64; 3]>,
  pub alpha: f64,
  pub view: View,
  pub input: Input,
  pub time: f64,
  pub dt: f64,
  pub player: u32,
  pub outbox: Vec<(String, Json)>,
  pub sounds: Vec<SoundOrder>,
  pub particles: Vec<Particle>,
  /// Слой поверх мира — в тайлах.
  pub draw: crate::ui::Batch,
  pub ui: Ui,
  pub looks: HashMap<u32, LookInfo>,
  /// Таблицы видов клиентской машины по номерам.
  pub protos: Vec<Table>,
}

impl ClientState {
  fn component(&self, name: &str) -> mlua::Result<usize> {
    self.world.borrow().world.schema.id(name).ok_or_else(|| mlua::Error::runtime(format!("нет компонента {name}")))
  }

  /// Место и направление сущности сейчас — между прошлым и этим тиком.
  pub fn pose(&self, entity: Entity) -> Option<[f64; 3]> {
    let state = self.world.borrow();
    let world = &state.world;
    let position = world.schema.id("Position")?;
    let facing = world.schema.id("Facing")?;
    let row = world.stores[position].row(entity)?;
    let x = world.stores[position].nums[0][row];
    let y = world.stores[position].nums[1][row];
    let angle = world.stores[facing].row(entity).map_or(0.0, |row| world.stores[facing].nums[0][row]);
    let Some(&[px, py, pa]) = self.prev.get(&entity) else { return Some([x, y, angle]) };
    let t = self.alpha;
    let turn = (angle - pa + std::f64::consts::PI).rem_euclid(std::f64::consts::TAU) - std::f64::consts::PI;
    Some([px + (x - px) * t, py + (y - py) * t, pa + turn * t])
  }

  /// Запоминает места перед тиком.
  pub fn remember(&mut self) {
    let state = self.world.borrow();
    let world = &state.world;
    let (Some(position), Some(facing)) = (world.schema.id("Position"), world.schema.id("Facing")) else { return };
    self.prev.clear();
    let store = &world.stores[position];
    for (row, &entity) in store.entities.iter().enumerate() {
      let angle = world.stores[facing].row(entity).map_or(0.0, |row| world.stores[facing].nums[0][row]);
      self.prev.insert(entity, [store.nums[0][row], store.nums[1][row], angle]);
    }
  }

  /// Сущность под точкой мира: юниты по радиусу, здания по прямоугольнику; ближайшая к точке.
  pub fn pick(&self, x: f64, y: f64) -> Option<Entity> {
    let state = self.world.borrow();
    let world = &state.world;
    let position = world.schema.id("Position")?;
    let unit = world.schema.id("Unit");
    let building = world.schema.id("Building");
    let mut best: Option<(f64, Entity)> = None;
    for &entity in &world.stores[position].entities {
      let Some([ex, ey, _]) = self.pose(entity) else { continue };
      let (dx, dy) = (x - ex, y - ey);
      let hit = if let Some(radius) = unit.and_then(|id| world.num(entity, id, "radius")) {
        dx * dx + dy * dy <= (radius + 0.15).powi(2)
      } else if let Some(id) = building.filter(|&id| world.has(entity, id)) {
        let (w, h) = (world.num(entity, id, "width").unwrap_or(1.0), world.num(entity, id, "height").unwrap_or(1.0));
        dx.abs() <= w / 2.0 && dy.abs() <= h / 2.0
      } else {
        false
      };
      if hit {
        let distance = dx * dx + dy * dy;
        if best.is_none_or(|(d, _)| distance < d) {
          best = Some((distance, entity));
        }
      }
    }
    best.map(|(_, entity)| entity)
  }
}

/// Цвет из Luau: 0xRRGGBB — непрозрачный, 0xAARRGGBB — с альфой в старшем байте; alpha — множитель.
pub fn color(value: u32, alpha: Option<f32>) -> [u8; 4] {
  let a = if value > 0xffffff { (value >> 24) as f32 / 255.0 } else { 1.0 } * alpha.unwrap_or(1.0);
  [(value >> 16) as u8, (value >> 8) as u8, value as u8, (a.clamp(0.0, 1.0) * 255.0).round() as u8]
}

/// `c` в скриптах клиента.
#[derive(Clone)]
pub struct Ctx {
  pub state: Rc<RefCell<ClientState>>,
  pub input: Table,
  pub camera: Table,
  pub shared: Table,
  pub atmosphere: Table,
  pub music: Table,
  pub protos: Table,
  pub game: Table,
}

fn entity(value: f64) -> mlua::Result<Entity> {
  if value >= 1.0 && value.fract() == 0.0 {
    Ok(value as Entity)
  } else {
    Err(mlua::Error::runtime(format!("{value} — не номер сущности")))
  }
}

impl UserData for Ctx {
  fn add_fields<F: UserDataFields<Self>>(fields: &mut F) {
    fields.add_field_method_get("input", |_, c| Ok(c.input.clone()));
    fields.add_field_method_get("camera", |_, c| Ok(c.camera.clone()));
    fields.add_field_method_get("shared", |_, c| Ok(c.shared.clone()));
    fields.add_field_method_get("atmosphere", |_, c| Ok(c.atmosphere.clone()));
    fields.add_field_method_get("music", |_, c| Ok(c.music.clone()));
    fields.add_field_method_get("protos", |_, c| Ok(c.protos.clone()));
    fields.add_field_method_get("game", |_, c| Ok(c.game.clone()));
    fields.add_field_method_get("time", |_, c| Ok(c.state.borrow().time));
    fields.add_field_method_get("dt", |_, c| Ok(c.state.borrow().dt));
    fields.add_field_method_get("player", |_, c| Ok(c.state.borrow().player));
    fields.add_field_method_get("tick", |_, c| Ok(c.state.borrow().world.borrow().tick));
    fields.add_field_method_get("draw", |_, c| Ok(Draw(c.state.clone())));
    fields.add_field_method_get("ui", |_, c| Ok(UiApi(c.state.clone())));
    fields.add_field_method_get("screen", |lua, c| {
      let state = c.state.borrow();
      let table = lua.create_table()?;
      table.set("w", state.view.screen[0])?;
      table.set("h", state.view.screen[1])?;
      Ok(table)
    });
  }

  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    methods.add_method("send", |lua, c, (name, args): (String, Option<Value>)| {
      let args: Json = lua.from_value(args.unwrap_or(Value::Nil))?;
      c.state.borrow_mut().outbox.push((name, args));
      Ok(())
    });
    // c:sound("click") или c:sound("motor", { x = 3, y = 4, gain = 0.5, pitch = 1.2, bus = "sfx" }).
    methods.add_method("sound", |_, c, (voice, options): (String, Option<Table>)| {
      let get = |key: &str| options.as_ref().and_then(|table| table.get::<Option<f64>>(key).ok().flatten());
      let at = match (get("x"), get("y")) {
        (Some(x), Some(y)) => Some([x as f32, y as f32]),
        _ => None,
      };
      let bus = options.as_ref().and_then(|table| table.get::<Option<String>>("bus").ok().flatten()).unwrap_or_else(|| "sfx".into());
      c.state.borrow_mut().sounds.push(SoundOrder { voice, at, gain: get("gain").unwrap_or(1.0) as f32, pitch: get("pitch").unwrap_or(1.0), bus });
      Ok(())
    });
    // c:particle({ x, y, vx, vy, life, color, size, drift }).
    methods.add_method("particle", |_, c, spec: Table| {
      let get = |key: &str, default: f32| spec.get::<Option<f32>>(key).map(|value| value.unwrap_or(default));
      let particle = Particle { x: get("x", 0.0)?, y: get("y", 0.0)?, vx: get("vx", 0.0)?, vy: get("vy", 0.0)?, age: 0.0, life: get("life", 1.0)?, color: spec.get::<Option<u32>>("color")?.unwrap_or(0xffffff), size: get("size", 1.0)?, drift: spec.get::<Option<bool>>("drift")?.unwrap_or(true) };
      let mut state = c.state.borrow_mut();
      if state.particles.len() < 20_000 {
        state.particles.push(particle);
      }
      Ok(())
    });
    // Сущности со всеми компонентами списка — номерами.
    methods.add_method("each", |_, c, components: Vec<String>| {
      let state = c.state.borrow();
      let ids = components.iter().map(|name| state.component(name)).collect::<mlua::Result<Vec<_>>>()?;
      let world = state.world.borrow();
      Ok(world.world.query(&ids))
    });
    // Значение компонента; Position и Facing — сглаженные между тиками.
    methods.add_method("get", |lua, c, (e, component): (f64, String)| {
      let e = entity(e)?;
      let state = c.state.borrow();
      let id = state.component(&component)?;
      let value = state.world.borrow().world.get(e, id);
      let Some(mut map) = value else { return Ok(Value::Nil) };
      if component == "Position" || component == "Facing" {
        if let Some([x, y, angle]) = state.pose(e) {
          if component == "Position" {
            map.insert("x".into(), Json::from(x));
            map.insert("y".into(), Json::from(y));
          } else {
            map.insert("angle".into(), Json::from(angle));
          }
        }
      }
      kharos_content::vm::to_lua(lua, &Json::Object(map))
    });
    methods.add_method("has", |_, c, (e, component): (f64, String)| {
      let state = c.state.borrow();
      let id = state.component(&component)?;
      let has = state.world.borrow().world.has(entity(e)?, id);
      Ok(has)
    });
    methods.add_method("alive", |_, c, e: f64| Ok(entity(e).is_ok_and(|e| c.state.borrow().world.borrow().world.alive(e))));
    // Вид сущности — таблица вида клиентской машины.
    methods.add_method("proto", |_, c, e: f64| {
      let state = c.state.borrow();
      let id = state.component("Proto")?;
      let proto = state.world.borrow().world.num(entity(e)?, id, "id");
      Ok(proto.and_then(|proto| state.protos.get(proto as usize).cloned()))
    });
    methods.add_method("mine", |_, c, e: f64| {
      let state = c.state.borrow();
      let id = state.component("Owner")?;
      let owner = state.world.borrow().world.num(entity(e)?, id, "player");
      Ok(owner == Some(state.player as f64))
    });
    methods.add_method("pick", |_, c, (x, y): (f64, f64)| Ok(c.state.borrow().pick(x, y)));
    // Сущности с Position в прямоугольнике мира и со всеми компонентами списка.
    methods.add_method("within", |_, c, (x1, y1, x2, y2, components): (f64, f64, f64, f64, Option<Vec<String>>)| {
      let state = c.state.borrow();
      let mut ids = components.unwrap_or_default().iter().map(|name| state.component(name)).collect::<mlua::Result<Vec<_>>>()?;
      ids.push(state.component("Position")?);
      let (left, right, top, bottom) = (x1.min(x2), x1.max(x2), y1.min(y2), y1.max(y2));
      let list = state.world.borrow().world.query(&ids);
      Ok(list.into_iter().filter(|&e| state.pose(e).is_some_and(|[x, y, _]| x >= left && x <= right && y >= top && y <= bottom)).collect::<Vec<_>>())
    });
    methods.add_method("to_screen", |_, c, (x, y): (f32, f32)| {
      let [sx, sy] = c.state.borrow().view.to_screen(x, y);
      Ok((sx, sy))
    });
    methods.add_method("to_world", |_, c, (x, y): (f32, f32)| {
      let [wx, wy] = c.state.borrow().view.to_world(x, y);
      Ok((wx, wy))
    });
  }
}

/// `c.draw` — поверх мира, в тайлах; толщины — в пикселях экрана.
pub struct Draw(Rc<RefCell<ClientState>>);

impl UserData for Draw {
  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    methods.add_method("line", |_, draw, (x1, y1, x2, y2, value, width, alpha): (f32, f32, f32, f32, u32, Option<f32>, Option<f32>)| {
      let mut state = draw.0.borrow_mut();
      let zoom = state.view.zoom;
      state.draw.line([x1, y1], [x2, y2], width.unwrap_or(1.0) / zoom, color(value, alpha));
      Ok(())
    });
    // Круг: width — толщина кольца в пикселях, без неё — заливка.
    methods.add_method("circle", |_, draw, (x, y, radius, value, width, alpha): (f32, f32, f32, u32, Option<f32>, Option<f32>)| {
      let mut state = draw.0.borrow_mut();
      let zoom = state.view.zoom;
      let segments = ((radius * zoom).sqrt() * 4.0).clamp(12.0, 64.0) as usize;
      state.draw.circle([x, y], radius, width.map(|width| width / zoom), color(value, alpha), segments);
      Ok(())
    });
    methods.add_method("rect", |_, draw, (x1, y1, x2, y2, value, width, alpha): (f32, f32, f32, f32, u32, Option<f32>, Option<f32>)| {
      let mut state = draw.0.borrow_mut();
      let zoom = state.view.zoom;
      let rgba = color(value, alpha);
      let (left, right, top, bottom) = (x1.min(x2), x1.max(x2), y1.min(y2), y1.max(y2));
      match width {
        None => state.draw.rect([left, top], [right, bottom], rgba),
        Some(width) => {
          let w = width / zoom;
          for (a, b) in [([left, top], [right, top]), ([right, top], [right, bottom]), ([right, bottom], [left, bottom]), ([left, bottom], [left, top])] {
            state.draw.line(a, b, w, rgba);
          }
        }
      }
      Ok(())
    });
  }
}

/// `c.ui` — интерфейс в логических пикселях экрана.
pub struct UiApi(Rc<RefCell<ClientState>>);

impl UserData for UiApi {
  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    methods.add_method("panel", |_, ui, (x, y, w, h, value, alpha): (f32, f32, f32, f32, Option<u32>, Option<f32>)| {
      ui.0.borrow_mut().ui.panel([x, y, w, h], color(value.unwrap_or(0x16161c), Some(alpha.unwrap_or(0.92))));
      Ok(())
    });
    methods.add_method("rect", |_, ui, (x, y, w, h, value, alpha): (f32, f32, f32, f32, u32, Option<f32>)| {
      ui.0.borrow_mut().ui.batch.rect([x, y], [x + w, y + h], color(value, alpha));
      Ok(())
    });
    methods.add_method("outline", |_, ui, (x, y, w, h, width, value, alpha): (f32, f32, f32, f32, f32, u32, Option<f32>)| {
      ui.0.borrow_mut().ui.outline([x, y, w, h], width, color(value, alpha));
      Ok(())
    });
    methods.add_method("line", |_, ui, (x1, y1, x2, y2, value, width, alpha): (f32, f32, f32, f32, u32, Option<f32>, Option<f32>)| {
      ui.0.borrow_mut().ui.batch.line([x1, y1], [x2, y2], width.unwrap_or(1.0), color(value, alpha));
      Ok(())
    });
    methods.add_method("circle", |_, ui, (x, y, radius, value, width, alpha): (f32, f32, f32, u32, Option<f32>, Option<f32>)| {
      ui.0.borrow_mut().ui.batch.circle([x, y], radius, width, color(value, alpha), 32);
      Ok(())
    });
    // Текст с левым верхним углом в (x, y); возвращает ширину.
    methods.add_method("text", |_, ui, (x, y, text, size, value, alpha): (f32, f32, String, Option<f32>, Option<u32>, Option<f32>)| Ok(ui.0.borrow_mut().ui.text([x, y], size.unwrap_or(12.0), color(value.unwrap_or(0xdedee4), alpha), &text)));
    methods.add_method("measure", |_, ui, (text, size): (String, Option<f32>)| {
      let mut state = ui.0.borrow_mut();
      let size = size.unwrap_or(12.0);
      let width = state.ui.font.width(&text, size);
      let height = state.ui.font.line_height(size);
      Ok((width, height))
    });
    // Кнопка: рамка, фон по наведению и нажатию, подпись по центру. Возвращает clicked, hovered.
    methods.add_method("button", |_, ui, (key, x, y, w, h, label, options): (String, f32, f32, f32, f32, Option<String>, Option<Table>)| {
      let get = |name: &str| options.as_ref().and_then(|table| table.get::<Option<Value>>(name).ok().flatten());
      let enabled = !matches!(get("enabled"), Some(Value::Boolean(false)));
      let selected = matches!(get("selected"), Some(Value::Boolean(true)));
      let size = match get("size") {
        Some(Value::Number(size)) => size as f32,
        Some(Value::Integer(size)) => size as f32,
        _ => 12.0,
      };
      let mut state = ui.0.borrow_mut();
      let response = if enabled { state.ui.interact(id(&key), [x, y, w, h]) } else { Default::default() };
      let background = match (enabled, selected, response.down, response.hovered) {
        (false, ..) => 0x24242c,
        (_, true, ..) => 0x1f5f8f,
        (_, _, true, _) => 0x60607a,
        (_, _, _, true) => 0x48485a,
        _ => 0x34343e,
      };
      state.ui.batch.rect([x, y], [x + w, y + h], color(background, None));
      state.ui.outline([x, y, w, h], 1.0, color(if response.hovered && enabled { 0x8a8aa8 } else { 0x46465a }, None));
      if let Some(label) = label {
        let width = state.ui.font.width(&label, size);
        let height = state.ui.font.line_height(size);
        let fg = if enabled { 0xdedee4 } else { 0x7a7a86 };
        state.ui.text([(x + (w - width) / 2.0).round(), (y + (h - height) / 2.0).round()], size, color(fg, None), &label);
      }
      Ok((response.clicked, response.hovered))
    });
    // Невидимая область: clicked, hovered, down.
    methods.add_method("area", |_, ui, (key, x, y, w, h): (String, f32, f32, f32, f32)| {
      let response = ui.0.borrow_mut().ui.interact(id(&key), [x, y, w, h]);
      Ok((response.clicked, response.hovered, response.down))
    });
    // Значок вида: кадр его облика (направление icon) размером size, стороной team (0 — своя, 1 — чужая, 2 — союзная).
    methods.add_method("icon", |_, ui, (proto, x, y, size, team): (Table, f32, f32, f32, Option<u32>)| {
      let id: u32 = proto.get("id")?;
      let mut state = ui.0.borrow_mut();
      let Some(info) = state.looks.get(&id) else { return Ok(()) };
      let uv = info.uv;
      let team = team.unwrap_or(0) as usize % crate::render::TEAMS;
      state.ui.batch.quad([x, y], [x + size, y + size], uv, [255; 4], Paint::Sprite { team });
      Ok(())
    });
    // Полоса: доля fraction заполнена цветом.
    methods.add_method("bar", |_, ui, (x, y, w, h, fraction, value, back): (f32, f32, f32, f32, f32, u32, Option<u32>)| {
      let mut state = ui.0.borrow_mut();
      state.ui.batch.rect([x, y], [x + w, y + h], color(back.unwrap_or(0x101014), None));
      state.ui.batch.rect([x, y], [x + w * fraction.clamp(0.0, 1.0), y + h], color(value, None));
      Ok(())
    });
    // Указатель над интерфейсом в этом кадре.
    methods.add_method("captured", |_, ui, ()| Ok(ui.0.borrow().ui.captured));
  }
}

/// Таблица ввода для скриптов: x, y — экран, wx, wy — мир, down/pressed/released по кнопкам, wheel, keys (зажатые),
/// hits (нажатые за кадр), text, over_ui (указатель над интерфейсом), focused, touch (указатель — касание).
pub fn fill_input(lua: &Lua, table: &Table, input: &Input, view: &View, over_ui: bool) -> mlua::Result<()> {
  let (x, y) = input.pointer.map_or((f32::NAN, f32::NAN), |[x, y]| (x, y));
  table.set("x", x)?;
  table.set("y", y)?;
  table.set("inside", input.pointer.is_some())?;
  let [wx, wy] = view.to_world(x, y);
  table.set("wx", wx)?;
  table.set("wy", wy)?;
  for (name, values) in [("down", input.down), ("pressed", input.pressed), ("released", input.released)] {
    let buttons = lua.create_table()?;
    for (index, button) in BUTTONS.iter().enumerate() {
      buttons.set(*button, values[index])?;
    }
    table.set(name, buttons)?;
  }
  table.set("wheel", input.wheel)?;
  let keys = lua.create_table()?;
  for key in &input.keys {
    keys.set(key.as_str(), true)?;
  }
  table.set("keys", keys)?;
  let hits = lua.create_table()?;
  for key in &input.hits {
    hits.set(key.as_str(), true)?;
  }
  table.set("hits", hits)?;
  table.set("text", input.text.as_str())?;
  table.set("over_ui", over_ui)?;
  table.set("focused", input.focused)?;
  table.set("touch", input.touch)?;
  Ok(())
}

/// Значок облика для `c.ui:icon` (после упаковки атласа).
pub fn look_info(look: &Look) -> LookInfo {
  LookInfo { uv: look.cells[look.cell(look.icon as f64 / look.directions.max(1) as f64 * crate::atlas::TURN, 0)] }
}
