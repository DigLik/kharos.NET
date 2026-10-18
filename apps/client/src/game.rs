//! Партия на клиенте: хост в том же процессе (одиночная игра), клиентская машина Luau и подача из модов. Кадр —
//! тики хоста по 50 мс с накоплением, потом скрипты подачи, звук и сборка кадра для рендера.

use crate::atlas::{self, Look};
use crate::audio::{Audio, Sound};
use crate::input::Input;
use crate::render::{Frame, View, LAYERS, SPRITE, TEAMS, TEXELS};
use crate::script::{fill_input, look_info, ClientState, Ctx, Particle};
use crate::ui::{Font, Ui};
use kharos_content::vm::function;
use kharos_content::{Content, Mod, Side};
use kharos_engine::terrain::{GeneratorConfig, Land, LandArea};
use kharos_world::{Entity, Host, Signal};
use mlua::{AnyUserData, Function, LuaSerdeExt, Table, Value};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

/// Длина тика хоста.
pub const STEP: f64 = 0.05;
/// Сколько тиков догонять за кадр, если кадр затянулся.
const MAX_TICKS: u32 = 5;
/// Сколько звуков юнитов звучит разом.
const UNIT_VOICES: usize = 8;

/// Палитра по умолчанию — TEAMS из unitArt.ts: свои синие, чужие красные, союзники зелёные; ничьи — серые.
const TEAMS_DEFAULT: [[u32; 3]; TEAMS] = [[0x1f63d8, 0x5aa9ff, 0xe4f4ff], [0xb3261e, 0xff6b5a, 0xffe9e4], [0x1f7a3c, 0x4ec973, 0xe8fff0], [0x1f63d8, 0x5aa9ff, 0xe4f4ff]];

/// Звуки атмосферы: вид, имя голоса в его `voices` и ручки.
struct Ambience {
  proto: Table,
  update: Option<Function>,
  knobs: Table,
  voices: Vec<(String, String)>,
}

pub struct Game {
  pub host: Host,
  pub client: Content,
  pub state: Rc<RefCell<ClientState>>,
  ctx: Ctx,
  ctx_data: AnyUserData,
  camera: Option<(Table, Function)>,
  panels: Vec<(String, Function)>,
  controls: Vec<(String, Function)>,
  ambience: Vec<Ambience>,
  music: Option<Table>,
  signals: HashMap<String, Vec<(String, Function)>>,
  pub looks: Vec<Look>,
  look_of: HashMap<u32, usize>,
  pub audio: Option<Audio>,
  held: HashMap<String, Sound>,
  /// Сглаженная нагрузка звучащих: сущность → 0..1.
  loads: HashMap<Entity, f32>,
  /// Музыка: номер следующего такта и когда он начнётся по часам звука.
  bar: u64,
  next_bar: f64,
  accumulator: f64,
  last: Option<f64>,
  pending: Vec<Signal>,
  palette: Vec<f32>,
  /// Общий атлас и земля — отдаются рендеру один раз.
  pub atlas: Option<(Vec<u8>, [usize; 2])>,
  pub land: Option<Land>,
  /// Пройденный путь сущностей на экране: от него — кадр хода (колёса, гусеницы, ноги), как в unitsPass.ts.
  walked: HashMap<Entity, Walk>,
}

/// Путь сущности: где её нарисовали в прошлый раз, сколько прошла и когда видели.
struct Walk {
  x: f32,
  y: f32,
  distance: f32,
  seen: f64,
}

fn rgb(value: u32) -> [f32; 3] {
  [((value >> 16) & 0xff) as f32 / 255.0, ((value >> 8) & 0xff) as f32 / 255.0, (value & 0xff) as f32 / 255.0]
}

fn error(what: impl std::fmt::Display) -> impl FnOnce(mlua::Error) -> String {
  move |error| format!("{what}: {error}")
}

/// Вид раздела по имени из настроек партии.
fn named(content: &Content, section: &str, name: &str) -> Result<Table, String> {
  content.get(section, name).ok_or_else(|| format!("партия просит {section}/{name}, а его нет"))
}

impl Game {
  pub fn new(mods: Vec<Mod>, game: Option<&str>, player: u32, font: Font) -> Result<Self, String> {
    let mut host = Host::new(mods.clone(), game, 1)?;
    let game = host.game.clone();
    let game = game.as_str();
    host.start()?;
    host.join(player)?;
    let client = Content::load(Side::Client, mods).map_err(|error| error.to_string())?;
    if client.checksum() != host.content.checksum() {
      return Err("моды хоста и клиента разошлись: суммы данных видов не совпали".into());
    }
    let lua = &client.lua;
    let game_table = named(&client, "games", game)?;
    let config: Table = game_table.get::<Option<Table>>("client").map_err(error("client партии"))?.unwrap_or(lua.create_table().map_err(error("client"))?);

    // Облики видов.
    let mut looks = Vec::new();
    let mut look_of = HashMap::new();
    for proto in &client.list {
      let table = client.by_id(proto.id).expect("вид");
      if let Some(look) = table.get::<Option<Table>>("look").map_err(error(format!("{}/{}.look", proto.section, proto.name)))? {
        if function(&look, "draw").map_err(error("look.draw"))?.is_none() {
          continue;
        }
        let built = atlas::build(lua, proto.id, look).map_err(error(format!("облик {}/{}", proto.section, proto.name)))?;
        look_of.insert(proto.id, looks.len());
        looks.push(built);
      }
    }
    let atlas = atlas::pack(&mut looks);

    let state = Rc::new(RefCell::new(ClientState { world: host.state.clone(), prev: HashMap::new(), alpha: 0.0, view: View { x: 0.0, y: 0.0, zoom: 32.0, screen: [1280.0, 800.0], scale: 1.0 }, input: Input::default(), time: 0.0, dt: 0.0, player, outbox: Vec::new(), sounds: Vec::new(), particles: Vec::new(), draw: Default::default(), ui: Ui::new(font), looks: looks.iter().map(|look| (look.proto, look_info(look))).collect(), protos: client.list.iter().map(|proto| client.by_id(proto.id).expect("вид")).collect() }));
    let table = || lua.create_table().map_err(error("таблица"));
    let ctx = Ctx { state: state.clone(), input: table()?, camera: table()?, shared: table()?, atmosphere: table()?, music: table()?, protos: client.protos.clone(), game: game_table.clone() };
    ctx.camera.set("x", 0.0).map_err(error("камера"))?;
    ctx.camera.set("y", 0.0).map_err(error("камера"))?;
    ctx.camera.set("zoom", 32.0).map_err(error("камера"))?;
    ctx.music.set("mood", "calm").map_err(error("музыка"))?;
    ctx.music.set("volume", 0.6).map_err(error("музыка"))?;
    let ctx_data = lua.create_userdata(ctx.clone()).map_err(error("c"))?;

    // Что берёт партия.
    let camera = match config.get::<Option<String>>("camera").map_err(error("client.camera"))? {
      Some(name) => {
        let proto = named(&client, "cameras", &name)?;
        let update = function(&proto, "update").map_err(error(format!("cameras/{name}")))?.ok_or_else(|| format!("cameras/{name}: нет update"))?;
        if let Some(start) = function(&proto, "start").map_err(error(format!("cameras/{name}")))? {
          start.call::<()>((&ctx.camera, &ctx_data)).map_err(error(format!("cameras/{name}.start")))?;
        }
        Some((proto, update))
      }
      None => None,
    };
    let list = |key: &str, section: &str, entry: &str| -> Result<Vec<(String, Function)>, String> {
      let names: Vec<String> = config.get::<Option<Vec<String>>>(key).map_err(error(format!("client.{key}")))?.unwrap_or_default();
      names
        .into_iter()
        .map(|name| {
          let proto = named(&client, section, &name)?;
          let run = function(&proto, entry).map_err(error(format!("{section}/{name}")))?.ok_or_else(|| format!("{section}/{name}: нет {entry}"))?;
          Ok((format!("{section}/{name}"), run))
        })
        .collect()
    };
    let panels = list("ui", "ui", "draw")?;
    let controls = list("controls", "controls", "update")?;

    let mut audio = Audio::new();
    if audio.is_none() {
      eprintln!("звука нет: некуда выводить");
    }
    if let Some(audio) = &mut audio {
      let voices: Vec<(String, Table)> = client.section("sounds").map(|proto| (proto.name.clone(), client.by_id(proto.id).expect("вид"))).collect();
      audio.load(lua, voices).map_err(error("голоса sounds/"))?;
    }

    let mut ambience = Vec::new();
    let mut held = HashMap::new();
    for name in config.get::<Option<Vec<String>>>("atmosphere").map_err(error("client.atmosphere"))?.unwrap_or_default() {
      let proto = named(&client, "atmosphere", &name)?;
      let update = function(&proto, "update").map_err(error(format!("atmosphere/{name}")))?;
      let knobs = table()?;
      let mut voices = Vec::new();
      if let Some(list) = proto.get::<Option<Table>>("voices").map_err(error(format!("atmosphere/{name}.voices")))? {
        for pair in list.pairs::<String, String>() {
          let (key, voice) = pair.map_err(error(format!("atmosphere/{name}.voices")))?;
          knobs.set(key.as_str(), table()?).map_err(error("ручки"))?;
          if let Some(audio) = &audio {
            if let Some(sound) = audio.start(lua, &voice, "ambience", None, None, 440.0, 1.0).map_err(error(format!("голос {voice}")))? {
              sound.place(0.0, 0.0);
              held.insert(format!("atmosphere/{name}/{key}"), sound);
            }
          }
          voices.push((key, voice));
        }
      }
      ambience.push(Ambience { proto, update, knobs, voices });
    }
    let music = match config.get::<Option<String>>("music").map_err(error("client.music"))? {
      Some(name) => Some(named(&client, "music", &name)?),
      None => None,
    };

    // Обработчики сигналов хоста.
    let mut signals: HashMap<String, Vec<(String, Function)>> = HashMap::new();
    for proto in &client.list {
      let table = client.by_id(proto.id).expect("вид");
      if let Some(list) = table.get::<Option<Table>>("signals").map_err(error(format!("{}/{}.signals", proto.section, proto.name)))? {
        for pair in list.pairs::<String, Function>() {
          let (name, handler) = pair.map_err(error(format!("{}/{}.signals", proto.section, proto.name)))?;
          signals.entry(name).or_default().push((format!("{}/{}", proto.section, proto.name), handler));
        }
      }
    }

    // Палитра сторон: своя, чужая, союзная (`teams = { own, foe, ally }`), ничья — серым поверх своей.
    let mut teams = TEAMS_DEFAULT;
    if let Some(table) = config.get::<Option<Table>>("teams").map_err(error("client.teams"))? {
      for (slot, key) in teams.iter_mut().zip(["own", "foe", "ally"]) {
        if let Some(colors) = table.get::<Option<Vec<u32>>>(key).map_err(error(format!("client.teams.{key}")))? {
          for (tone, value) in slot.iter_mut().zip(colors) {
            *tone = value;
          }
        }
      }
    }
    teams[3] = teams[0];
    let palette: Vec<f32> = teams.iter().flat_map(|team| team.iter().flat_map(|&value| rgb(value))).collect();

    // Земля: генератор из вида `lands/` партии (data.land), карта — data.size тайлов от центра.
    let data = game_table.get::<Option<Table>>("data").map_err(error("data партии"))?;
    let land_name = data.as_ref().and_then(|data| data.get::<Option<String>>("land").ok().flatten());
    let land = match land_name {
      Some(name) => {
        let kind = named(&client, "lands", &name)?;
        let generator: serde_json::Value = lua.from_value(kind.get::<Value>("data").map_err(error(format!("lands/{name}.data")))?).map_err(error(format!("lands/{name}.data")))?;
        let config: GeneratorConfig = serde_json::from_value(generator).map_err(|problem| format!("lands/{name}.data: {problem}"))?;
        let size = data.as_ref().and_then(|data| data.get::<Option<i32>>("size").ok().flatten()).unwrap_or(64);
        Some(Land::new(config, LandArea::of(-size, -size, size, size)))
      }
      None => None,
    };

    Ok(Self { host, client, state, ctx, ctx_data, camera, panels, controls, ambience, music, signals, looks, look_of, atlas: Some(atlas), land, audio, held, loads: HashMap::new(), walked: HashMap::new(), bar: 0, next_bar: 0.0, accumulator: 0.0, last: None, pending: Vec::new(), palette })
  }

  /// Кадр: тики, подача, звук. Возвращает, что рисовать.
  pub fn frame(&mut self, input: &Input) -> Result<(Frame, View), String> {
    let dt = self.last.map_or(1.0 / 60.0, |last| (input.time - last).clamp(0.0, 0.25));
    self.last = Some(input.time);
    self.accumulator += dt;
    let mut ticks = 0;
    while self.accumulator >= STEP {
      if ticks == MAX_TICKS {
        self.accumulator = 0.0;
        break;
      }
      self.state.borrow_mut().remember();
      self.host.tick(STEP)?;
      self.pending.extend(self.host.signals());
      self.accumulator -= STEP;
      ticks += 1;
    }
    {
      let mut state = self.state.borrow_mut();
      state.alpha = self.accumulator / STEP;
      state.time = input.time;
      state.dt = dt;
      state.input = input.clone();
      state.view.screen = input.screen;
      state.view.scale = input.scale;
      state.draw.clear();
      state.ui.begin(input);
    }
    let lua = self.client.lua.clone();
    let over_ui = self.state.borrow().ui.was_captured;
    fill_input(&lua, &self.ctx.input, input, &self.state.borrow().view, over_ui).map_err(error("ввод"))?;

    // Сигналы хоста.
    for signal in std::mem::take(&mut self.pending) {
      let Some(handlers) = self.signals.get(&signal.name) else { continue };
      let payload = kharos_content::vm::to_lua(&lua, &signal.payload).map_err(error(&signal.name))?;
      for (label, handler) in handlers {
        handler.call::<()>((&self.ctx_data, payload.clone())).map_err(error(format!("{label}.signals.{}", signal.name)))?;
      }
    }

    // Камера.
    if let Some((_, update)) = &self.camera {
      update.call::<()>((&self.ctx.camera, &self.ctx_data)).map_err(error("камера"))?;
    }
    {
      let camera = &self.ctx.camera;
      let mut state = self.state.borrow_mut();
      state.view.x = camera.get::<f32>("x").unwrap_or(0.0);
      state.view.y = camera.get::<f32>("y").unwrap_or(0.0);
      state.view.zoom = camera.get::<f32>("zoom").unwrap_or(32.0).clamp(2.0, 256.0);
    }
    let view = self.state.borrow().view;
    fill_input(&lua, &self.ctx.input, input, &view, over_ui).map_err(error("ввод"))?;

    // Панели, потом управление: указатель над панелью этого кадра миру не достаётся.
    for (label, draw) in &self.panels {
      draw.call::<()>(&self.ctx_data).map_err(error(label))?;
    }
    let captured = self.state.borrow().ui.captured || over_ui;
    self.ctx.input.set("over_ui", captured).map_err(error("ввод"))?;
    for (label, update) in &self.controls {
      update.call::<()>(&self.ctx_data).map_err(error(label))?;
    }

    // Атмосфера.
    for ambience in &self.ambience {
      if let Some(update) = &ambience.update {
        update.call::<()>((&self.ctx.atmosphere, &self.ctx_data, &ambience.knobs)).map_err(error(format!("atmosphere/{}", ambience.proto.get::<String>("name").unwrap_or_default())))?;
      }
    }
    self.sound(&view)?;
    self.particles(dt as f32);
    self.state.borrow_mut().ui.end();

    let (player, outbox) = {
      let mut state = self.state.borrow_mut();
      (state.player, std::mem::take(&mut state.outbox))
    };
    for (name, args) in outbox {
      self.host.send(player, &name, args);
    }
    Ok((self.build(&view, input.time)?, view))
  }

  /// Звук кадра: заказанные скриптами, юниты рядом с камерой, атмосфера, музыка.
  fn sound(&mut self, view: &View) -> Result<(), String> {
    let lua = self.client.lua.clone();
    let orders = std::mem::take(&mut self.state.borrow_mut().sounds);
    let Some(audio) = &self.audio else { return Ok(()) };
    let hearing = (view.screen[0].max(view.screen[1]) / view.zoom * 0.75).max(8.0);
    let place = |x: f32, y: f32| {
      let (dx, dy) = (x - view.x, y - view.y);
      let distance = (dx * dx + dy * dy).sqrt() / hearing;
      let gain = (1.0 - distance).clamp(0.0, 1.0).powf(1.5) * (view.zoom / 32.0).clamp(0.35, 1.0);
      (gain, dx / (view.screen[0] / view.zoom / 2.0))
    };
    for order in orders {
      let (gain, pan) = order.at.map_or((1.0, 0.0), |[x, y]| place(x, y));
      if gain > 0.01 {
        audio.play(&lua, &order.voice, &order.bus, gain * order.gain, pan, order.pitch).map_err(error(format!("звук {}", order.voice)))?;
      }
    }

    // Звуки юнитов: ближайшие к камере, у кого в облике есть sound.
    let mut candidates: Vec<(f32, Entity, usize)> = Vec::new();
    {
      let state = self.state.borrow();
      let world = state.world.borrow();
      let schema = &world.world.schema;
      let (Some(position), Some(proto)) = (schema.id("Position"), schema.id("Proto")) else { return Ok(()) };
      let store = &world.world.stores[position];
      for &entity in &store.entities {
        let Some(id) = world.world.num(entity, proto, "id") else { continue };
        let Some(&look) = self.look_of.get(&(id as u32)) else { continue };
        if !matches!(self.looks[look].table.get::<Value>("sound"), Ok(Value::Table(_))) {
          continue;
        }
        let Some([x, y, _]) = state.pose(entity) else { continue };
        let distance = ((x as f32 - view.x).powi(2) + (y as f32 - view.y).powi(2)).sqrt();
        if distance < hearing {
          candidates.push((distance, entity, look));
        }
      }
    }
    candidates.sort_by(|a, b| a.0.total_cmp(&b.0));
    candidates.truncate(UNIT_VOICES);
    let drive = self.state.borrow().world.borrow().world.schema.id("Drive");
    let mut alive = std::collections::HashSet::new();
    for &(_, entity, look) in &candidates {
      let key = format!("unit/{entity}");
      alive.insert(key.clone());
      let spec: Table = self.looks[look].table.get("sound").map_err(error("look.sound"))?;
      let voice: String = spec.get("voice").map_err(error("look.sound.voice"))?;
      if !self.held.contains_key(&key) {
        if let Some(sound) = audio.start(&lua, &voice, "units", None, None, 440.0, 1.0).map_err(error(format!("голос {voice}")))? {
          sound.place(0.0, 0.0);
          self.held.insert(key.clone(), sound);
        }
      }
      let moving = drive.and_then(|id| self.state.borrow().world.borrow().world.num(entity, id, "moving")).unwrap_or(0.0) > 0.0;
      let load = self.loads.entry(entity).or_insert(0.0);
      *load += ((if moving { 1.0 } else { 0.0 }) - *load) * (self.state.borrow().dt as f32 * 3.0).min(1.0);
      let unit = lua.create_table().map_err(error("звук"))?;
      unit.set("entity", entity).map_err(error("звук"))?;
      unit.set("load", *load).map_err(error("звук"))?;
      unit.set("moving", moving).map_err(error("звук"))?;
      let knobs = lua.create_table().map_err(error("звук"))?;
      knobs.set("load", *load).map_err(error("звук"))?;
      if let Some(control) = function(&spec, "control").map_err(error("look.sound.control"))? {
        control.call::<()>((&knobs, &unit, &self.ctx_data)).map_err(error(format!("{voice}: control")))?;
      }
      if let Some(sound) = self.held.get(&key) {
        sound.knobs(&lua, &knobs).map_err(error(format!("голос {voice}: update")))?;
        let [x, y, _] = self.state.borrow().pose(entity).unwrap_or([0.0; 3]);
        let (gain, pan) = place(x as f32, y as f32);
        let base = spec.get::<Option<f32>>("gain").ok().flatten().unwrap_or(1.0) * knobs.get::<Option<f32>>("gain").ok().flatten().unwrap_or(1.0);
        sound.place(gain * base, pan);
      }
    }
    let gone: Vec<String> = self.held.keys().filter(|key| key.starts_with("unit/") && !alive.contains(*key)).cloned().collect();
    for key in gone {
      if let Some(sound) = self.held.remove(&key) {
        sound.release(0.3);
      }
    }
    self.loads.retain(|entity, _| alive.contains(&format!("unit/{entity}")));

    // Атмосфера: ручки её голосов.
    for ambience in &self.ambience {
      let name: String = ambience.proto.get("name").unwrap_or_default();
      for (key, voice) in &ambience.voices {
        let Some(sound) = self.held.get(&format!("atmosphere/{name}/{key}")) else { continue };
        let knobs: Table = ambience.knobs.get(key.as_str()).map_err(error("ручки"))?;
        sound.knobs(&lua, &knobs).map_err(error(format!("голос {voice}: update")))?;
        sound.place(knobs.get::<Option<f32>>("gain").ok().flatten().unwrap_or(1.0), knobs.get::<Option<f32>>("pan").ok().flatten().unwrap_or(0.0));
      }
    }

    // Музыка: сочинитель — раз в такт, ноты расписываются наперёд по часам звука.
    if let Some(music) = &self.music {
      audio.set_volume("music", self.ctx.music.get::<Option<f32>>("volume").ok().flatten().unwrap_or(0.6));
      let now = audio.now();
      if self.next_bar < now {
        self.next_bar = now + 0.2;
      }
      if self.next_bar - now < 0.5 {
        let tempo = music.get::<Option<f64>>("tempo").ok().flatten().unwrap_or(90.0);
        let beats = music.get::<Option<f64>>("beats").ok().flatten().unwrap_or(4.0);
        let beat = 60.0 / tempo;
        if let Some(compose) = function(music, "compose").map_err(error("music.compose"))? {
          let notes: Option<Vec<Table>> = compose.call((self.bar, &self.ctx_data)).map_err(error("music.compose"))?;
          for note in notes.unwrap_or_default() {
            let get = |key: &str, default: f64| note.get::<Option<f64>>(key).ok().flatten().unwrap_or(default);
            let voice: String = note.get("voice").map_err(error("нота: voice"))?;
            let at = self.next_bar + get("beat", 0.0) * beat;
            let length = get("length", 1.0) * beat;
            let pitch = 440.0 * 2f64.powf((get("note", 69.0) - 69.0) / 12.0);
            if let Some(sound) = audio.start(&lua, &voice, "music", Some(at), Some(length), pitch, get("velocity", 0.7)).map_err(error(format!("нота {voice}")))? {
              sound.place(get("gain", 1.0) as f32, get("pan", 0.0) as f32);
            }
          }
        }
        self.bar += 1;
        self.next_bar += beats * beat;
      }
    }
    Ok(())
  }

  /// Частицы: ход, снос ветром из атмосферы, отмирание; рисуются в слой поверх мира.
  fn particles(&mut self, dt: f32) {
    let wind = self.ctx.atmosphere.get::<Option<Table>>("wind").ok().flatten();
    let (wx, wy) = wind.map_or((0.0, 0.0), |wind| (wind.get::<f32>("x").unwrap_or(0.0), wind.get::<f32>("y").unwrap_or(0.0)));
    let mut state = self.state.borrow_mut();
    let mut particles = std::mem::take(&mut state.particles);
    particles.retain_mut(|particle: &mut Particle| {
      particle.age += dt;
      let drift = if particle.drift { 1.0 } else { 0.0 };
      particle.x += (particle.vx + wx * drift) * dt;
      particle.y += (particle.vy + wy * drift) * dt;
      particle.age < particle.life
    });
    for particle in &particles {
      let fade = 1.0 - particle.age / particle.life;
      let size = particle.size / TEXELS;
      let x = (particle.x * TEXELS).floor() / TEXELS;
      let y = (particle.y * TEXELS).floor() / TEXELS;
      let color = crate::script::color(particle.color, Some(fade.min(1.0)));
      state.draw.rect([x, y], [x + size, y + size], color);
    }
    state.particles = particles;
  }

  /// Кадр для рендера: спрайты видимых сущностей по слоям, огни, свет атмосферы.
  fn build(&mut self, view: &View, time: f64) -> Result<Frame, String> {
    let mut frame = Frame { layers: vec![Vec::new(); LAYERS], palette: self.palette.clone(), time: time as f32, ..Default::default() };
    let light = self.ctx.atmosphere.get::<Option<Table>>("light").ok().flatten();
    frame.ambient = light.map_or([1.0; 3], |light| [light.get::<f32>("r").unwrap_or(1.0), light.get::<f32>("g").unwrap_or(1.0), light.get::<f32>("b").unwrap_or(1.0)]);
    frame.lamps = self.ctx.atmosphere.get::<Option<f32>>("lamps").ok().flatten().unwrap_or(0.0).clamp(0.0, 1.0);
    let state = self.state.borrow();
    let world = state.world.borrow();
    let schema = &world.world.schema;
    let (Some(position), Some(proto), Some(owner)) = (schema.id("Position"), schema.id("Proto"), schema.id("Owner")) else { return Ok(frame) };
    let attached = schema.id("Attached");
    let bounds = view.bounds(7.0);
    let snap = |value: f32| (value * TEXELS).round() / TEXELS;
    let lights = frame.lamps > 0.0;
    // Слой и порядок внутри слоя (здания — по нижнему краю: нижние перекрывают верхние).
    let mut sorted: Vec<(usize, f32, [f32; SPRITE])> = Vec::new();
    let layer_of = |entity: Entity| -> Option<usize> {
      let id = world.world.num(entity, proto, "id")?;
      let look = &self.looks[*self.look_of.get(&(id as u32))?];
      Some(look.layer as usize)
    };
    for &entity in &world.world.stores[position].entities {
      let Some(id) = world.world.num(entity, proto, "id") else { continue };
      let Some(&index) = self.look_of.get(&(id as u32)) else { continue };
      let Some([x, y, angle]) = state.pose(entity) else { continue };
      let (x, y) = (x as f32, y as f32);
      if x < bounds[0] || x > bounds[2] || y < bounds[1] || y > bounds[3] {
        continue;
      }
      let look = &self.looks[index];
      // Турель — слоем выше своего носителя.
      let parent = attached.and_then(|id| world.world.num(entity, id, "parent")).filter(|&parent| parent >= 1.0);
      let layer = match parent {
        Some(parent) => layer_of(parent as Entity).unwrap_or(0) + 1,
        None => look.layer as usize,
      };
      // Кадр: у ходовых — по пройденному пути, у остальных — по времени.
      let phase = match look.step {
        Some(step) if look.phases > 1 => {
          let walk = self.walked.entry(entity).or_insert(Walk { x, y, distance: 0.0, seen: time });
          let moved = (x - walk.x).hypot(y - walk.y);
          if moved < 1.0 {
            walk.distance += moved;
          }
          walk.x = x;
          walk.y = y;
          walk.seen = time;
          (walk.distance / step) as usize % look.phases
        }
        _ if look.phases > 1 => (time * look.fps as f64) as usize % look.phases,
        _ => 0,
      };
      let player = world.world.num(entity, owner, "player").unwrap_or(0.0) as u32;
      let (team, grey) = if player == 0 {
        (3.0, 1.0)
      } else if player == state.player {
        (0.0, 0.0)
      } else {
        (1.0, 0.0)
      };
      let cell = look.cell(angle, phase);
      let [u0, v0, u1, v1] = look.cells[cell];
      let [w, h] = look.tiles();
      let (left, top) = (x + look.offset[0] - view.x, y + look.offset[1] - view.y);
      sorted.push((layer, y + look.offset[1] + h, [left, top, w, h, u0, v0, u1, v1, team, look.shadow[0], look.shadow[1], grey]));
      if !lights {
        continue;
      }
      let (cos, sin) = ((angle as f32).cos(), (angle as f32).sin());
      let (forward_x, forward_y) = (cos / TEXELS, sin / TEXELS);
      for &[along, across, glow] in &look.lights.lamps {
        let (lx, ly) = (snap(x + along * forward_x - across * forward_y), snap(y + along * forward_y + across * forward_x));
        frame.spots.extend_from_slice(&[lx, ly, glow * 2.0, 1.0]);
        frame.blooms.extend_from_slice(&[lx, ly, glow, 1.0]);
      }
      if let Some([along, length, near, spread, level]) = look.lights.beam {
        frame.beams.extend_from_slice(&[snap(x + along * forward_x), snap(y + along * forward_y), length, level, cos, sin, near, spread]);
      }
      // Огни кадра (здания): как lights.add в buildingsPass.ts — пятно в 16 радиусов лампы, ореол в 3 + 2.
      for &[lx, ly, size, level] in &look.frame_lights[cell] {
        if level <= 0.0 {
          continue;
        }
        frame.spots.extend_from_slice(&[x + lx, y + ly, size * 16.0, level]);
        frame.blooms.extend_from_slice(&[x + lx, y + ly, size * 3.0 + 2.0, level]);
      }
    }
    sorted.sort_by(|a, b| a.0.cmp(&b.0).then(if a.0 == 2 { a.1.total_cmp(&b.1) } else { std::cmp::Ordering::Equal }));
    for (layer, _, sprite) in sorted {
      frame.layers[layer.min(LAYERS - 1)].extend_from_slice(&sprite);
    }
    drop(world);
    drop(state);
    self.walked.retain(|_, walk| time - walk.seen < 2.0);
    Ok(frame)
  }

  pub fn palette(&self) -> &[f32] {
    &self.palette
  }
}
