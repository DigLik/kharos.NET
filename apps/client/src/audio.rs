//! Синтезатор: голоса из `sounds/` строят граф узлов Web Audio (крейт `web-audio-api` на нативе, в браузере —
//! `AudioContext`). Звук считает движок, Luau только строит схему и двигает ручки.
//!
//! Голос:
//!
//! ```luau
//! return {
//!   -- Схема: s — строитель. s.at — когда звучать, s.length — сколько (nil — пока не отпустят), s.note — частота
//!   -- ноты в герцах, s.velocity — сила. Вернуть таблицу узлов — её получит update.
//!   graph = function(s)
//!     local osc = s:osc("sawtooth", 55)
//!     local filter = s:filter("lowpass", 400, 1)
//!     osc:to(filter):to(s:env({ attack = 0.01, release = 0.3 })):to(s:out())
//!     return { osc = osc, filter = filter }
//!   end,
//!   -- Раз в кадр у голосов, которые звучат долго: ручки — что дал владелец звука.
//!   update = function(nodes, knobs) nodes.osc.frequency = 40 + knobs.load * 60 end,
//!   -- Или запечённый звук: функция пишет отсчёты f32 в буфер один раз при загрузке.
//!   bake = { length = 0.2, render = function(out, rate, n) end },
//! }
//! ```

use mlua::{Function, Lua, MetaMethod, RegistryKey, Table, UserData, UserDataFields, UserDataMethods, Value};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use web_audio_api::context::{AudioContext, BaseAudioContext};
use web_audio_api::node::{AudioBufferSourceNode, AudioNode, AudioScheduledSourceNode, BiquadFilterNode, BiquadFilterType, ConstantSourceNode, DelayNode, GainNode, OscillatorNode, OscillatorType, StereoPannerNode, WaveShaperNode};
use web_audio_api::{AudioBuffer, AudioParam};

/// Шины: всё сходится в общую громкость.
pub const BUSES: [&str; 4] = ["sfx", "units", "ambience", "music"];

enum Node {
  Osc(OscillatorNode),
  Source(AudioBufferSourceNode),
  Const(ConstantSourceNode),
  Filter(BiquadFilterNode),
  Gain(GainNode),
  Delay(DelayNode),
  Shaper(WaveShaperNode),
  Pan(StereoPannerNode),
}

impl Node {
  fn node(&self) -> &dyn AudioNode {
    match self {
      Node::Osc(node) => node,
      Node::Source(node) => node,
      Node::Const(node) => node,
      Node::Filter(node) => node,
      Node::Gain(node) => node,
      Node::Delay(node) => node,
      Node::Shaper(node) => node,
      Node::Pan(node) => node,
    }
  }

  fn param(&self, name: &str) -> Option<&AudioParam> {
    Some(match (self, name) {
      (Node::Osc(node), "frequency") => node.frequency(),
      (Node::Osc(node), "detune") => node.detune(),
      (Node::Source(node), "rate") => node.playback_rate(),
      (Node::Source(node), "detune") => node.detune(),
      (Node::Const(node), "offset") => node.offset(),
      (Node::Filter(node), "frequency") => node.frequency(),
      (Node::Filter(node), "q") => node.q(),
      (Node::Filter(node), "gain") => node.gain(),
      (Node::Filter(node), "detune") => node.detune(),
      (Node::Gain(node), "gain") => node.gain(),
      (Node::Delay(node), "time") => node.delay_time(),
      (Node::Pan(node), "pan") => node.pan(),
      _ => return None,
    })
  }

  fn start(&mut self, at: f64) {
    match self {
      Node::Osc(node) => node.start_at(at),
      Node::Source(node) => node.start_at(at),
      Node::Const(node) => node.start_at(at),
      _ => {}
    }
  }

  fn stop(&mut self, at: f64) {
    match self {
      Node::Osc(node) => node.stop_at(at),
      Node::Source(node) => node.stop_at(at),
      Node::Const(node) => node.stop_at(at),
      _ => {}
    }
  }
}

/// Общее у синтезатора и строителей голосов.
pub struct Engine {
  pub ctx: AudioContext,
  pub master: GainNode,
  pub buses: HashMap<&'static str, GainNode>,
  /// Шум на две секунды по кругу: белый, розовый, коричневый.
  noise: [AudioBuffer; 3],
}

impl Engine {
  fn now(&self) -> f64 {
    self.ctx.current_time()
  }
}

/// Узлы одного звучания голоса.
struct Nodes {
  list: Vec<Node>,
  /// Когда отпущена огибающая и сколько звучит её затухание.
  release: f64,
  engine: Rc<Engine>,
  /// Узел, куда голос пишет свой выход: громкость звучания.
  out: usize,
}

/// Узел в Luau: номер в звучании.
#[derive(Clone)]
struct NodeRef {
  nodes: Rc<RefCell<Nodes>>,
  index: usize,
}

/// Параметр узла в Luau.
#[derive(Clone)]
struct ParamRef {
  node: NodeRef,
  name: String,
}

impl ParamRef {
  fn with<R>(&self, apply: impl FnOnce(&AudioParam, f64) -> R) -> mlua::Result<R> {
    let nodes = self.node.nodes.borrow();
    let now = nodes.engine.now();
    let param = nodes.list[self.node.index].param(&self.name).ok_or_else(|| mlua::Error::runtime(format!("у узла нет параметра {}", self.name)))?;
    Ok(apply(param, now))
  }
}

fn connect(from: &NodeRef, to: &Value) -> mlua::Result<()> {
  let nodes = from.nodes.borrow();
  let source = nodes.list[from.index].node();
  match to {
    Value::UserData(data) if data.is::<NodeRef>() => {
      let target = data.borrow::<NodeRef>()?;
      if !Rc::ptr_eq(&target.nodes, &from.nodes) {
        return Err(mlua::Error::runtime("узлы разных звучаний не соединить"));
      }
      source.connect(nodes.list[target.index].node());
    }
    Value::UserData(data) if data.is::<ParamRef>() => {
      let target = data.borrow::<ParamRef>()?;
      let param = nodes.list[target.node.index].param(&target.name).ok_or_else(|| mlua::Error::runtime(format!("нет параметра {}", target.name)))?;
      source.connect(param);
    }
    _ => return Err(mlua::Error::runtime("to: нужен узел или его параметр")),
  }
  Ok(())
}

impl UserData for NodeRef {
  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    // a:to(b) — соединить; возвращает b, чтобы строить цепочкой.
    methods.add_method("to", |_, node, target: Value| {
      connect(node, &target)?;
      Ok(target)
    });
    methods.add_meta_method(MetaMethod::Index, |_, node, name: String| Ok(ParamRef { node: node.clone(), name }));
    // node.frequency = 220 — плавно, за 20 мс: ручки меняются раз в кадр, без щелчков.
    methods.add_meta_method(MetaMethod::NewIndex, |_, node, (name, value): (String, f32)| {
      ParamRef { node: node.clone(), name }.with(|param, now| {
        param.set_target_at_time(value, now, 0.02);
      })
    });
  }
}

impl UserData for ParamRef {
  fn add_fields<F: UserDataFields<Self>>(fields: &mut F) {
    fields.add_field_method_get("value", |_, param| param.with(|param, _| param.value()));
  }

  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    // set(v, at?) — сразу или в миг at; ramp(v, at) — линейно к at; exp(v, at) — по экспоненте;
    // target(v, at, tau) — плавно с постоянной tau.
    methods.add_method("set", |_, param, (value, at): (f32, Option<f64>)| {
      param.with(|param, now| {
        param.set_value_at_time(value, at.unwrap_or(now));
      })
    });
    methods.add_method("ramp", |_, param, (value, at): (f32, f64)| {
      param.with(|param, _| {
        param.linear_ramp_to_value_at_time(value, at);
      })
    });
    methods.add_method("exp", |_, param, (value, at): (f32, f64)| {
      param.with(|param, _| {
        param.exponential_ramp_to_value_at_time(value.max(1e-4), at);
      })
    });
    methods.add_method("target", |_, param, (value, at, tau): (f32, f64, f64)| {
      param.with(|param, _| {
        param.set_target_at_time(value, at, tau.max(1e-4));
      })
    });
  }
}

/// Строитель схемы голоса.
struct Builder {
  nodes: Rc<RefCell<Nodes>>,
  at: f64,
  length: Option<f64>,
  note: f64,
  velocity: f64,
}

impl Builder {
  fn push(&self, node: Node) -> NodeRef {
    let mut nodes = self.nodes.borrow_mut();
    let mut node = node;
    node.start(self.at);
    nodes.list.push(node);
    NodeRef { nodes: self.nodes.clone(), index: nodes.list.len() - 1 }
  }

  fn engine(&self) -> Rc<Engine> {
    self.nodes.borrow().engine.clone()
  }
}

fn wave(name: &str) -> OscillatorType {
  match name {
    "square" => OscillatorType::Square,
    "sawtooth" | "saw" => OscillatorType::Sawtooth,
    "triangle" => OscillatorType::Triangle,
    _ => OscillatorType::Sine,
  }
}

fn filter_type(name: &str) -> BiquadFilterType {
  match name {
    "highpass" => BiquadFilterType::Highpass,
    "bandpass" => BiquadFilterType::Bandpass,
    "notch" => BiquadFilterType::Notch,
    "allpass" => BiquadFilterType::Allpass,
    "peaking" => BiquadFilterType::Peaking,
    "lowshelf" => BiquadFilterType::Lowshelf,
    "highshelf" => BiquadFilterType::Highshelf,
    _ => BiquadFilterType::Lowpass,
  }
}

impl UserData for Builder {
  fn add_fields<F: UserDataFields<Self>>(fields: &mut F) {
    fields.add_field_method_get("at", |_, s| Ok(s.at));
    fields.add_field_method_get("length", |_, s| Ok(s.length));
    fields.add_field_method_get("note", |_, s| Ok(s.note));
    fields.add_field_method_get("velocity", |_, s| Ok(s.velocity));
    fields.add_field_method_get("rate", |_, s| Ok(s.engine().ctx.sample_rate()));
  }

  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    methods.add_method("osc", |_, s, (kind, frequency): (Option<String>, Option<f32>)| {
      let mut osc = s.engine().ctx.create_oscillator();
      osc.set_type(wave(kind.as_deref().unwrap_or("sine")));
      osc.frequency().set_value(frequency.unwrap_or(s.note as f32));
      Ok(s.push(Node::Osc(osc)))
    });
    // Шум по кругу: "white", "pink", "brown".
    methods.add_method("noise", |_, s, color: Option<String>| {
      let engine = s.engine();
      let mut source = engine.ctx.create_buffer_source();
      let index = match color.as_deref() {
        Some("pink") => 1,
        Some("brown") => 2,
        _ => 0,
      };
      source.set_buffer(engine.noise[index].clone());
      source.set_loop(true);
      Ok(s.push(Node::Source(source)))
    });
    methods.add_method("const", |_, s, value: Option<f32>| {
      let node = s.engine().ctx.create_constant_source();
      node.offset().set_value(value.unwrap_or(1.0));
      Ok(s.push(Node::Const(node)))
    });
    methods.add_method("filter", |_, s, (kind, frequency, q): (Option<String>, Option<f32>, Option<f32>)| {
      let mut filter = s.engine().ctx.create_biquad_filter();
      filter.set_type(filter_type(kind.as_deref().unwrap_or("lowpass")));
      filter.frequency().set_value(frequency.unwrap_or(1000.0));
      filter.q().set_value(q.unwrap_or(1.0));
      Ok(s.push(Node::Filter(filter)))
    });
    methods.add_method("gain", |_, s, value: Option<f32>| {
      let gain = s.engine().ctx.create_gain();
      gain.gain().set_value(value.unwrap_or(1.0));
      Ok(s.push(Node::Gain(gain)))
    });
    methods.add_method("delay", |_, s, (time, max): (Option<f64>, Option<f64>)| {
      let delay = s.engine().ctx.create_delay(max.unwrap_or(2.0));
      delay.delay_time().set_value(time.unwrap_or(0.25) as f32);
      Ok(s.push(Node::Delay(delay)))
    });
    // Искажение: amount 0..1 и больше — насколько жёстко срезаются пики.
    methods.add_method("shape", |_, s, amount: Option<f32>| {
      let mut shaper = s.engine().ctx.create_wave_shaper();
      let k = amount.unwrap_or(0.5) * 50.0;
      let curve: Vec<f32> = (0..1024).map(|i| i as f32 / 511.5 - 1.0).map(|x| (1.0 + k) * x / (1.0 + k * x.abs())).collect();
      shaper.set_curve(curve);
      Ok(s.push(Node::Shaper(shaper)))
    });
    methods.add_method("pan", |_, s, value: Option<f32>| {
      let pan = s.engine().ctx.create_stereo_panner();
      pan.pan().set_value(value.unwrap_or(0.0));
      Ok(s.push(Node::Pan(pan)))
    });
    // Колебание для параметра: синус частоты frequency размахом depth — s:lfo(5, 20):to(osc.frequency).
    methods.add_method("lfo", |_, s, (frequency, depth, kind): (f32, f32, Option<String>)| {
      let engine = s.engine();
      let mut osc = engine.ctx.create_oscillator();
      osc.set_type(wave(kind.as_deref().unwrap_or("sine")));
      osc.frequency().set_value(frequency);
      let gain = engine.ctx.create_gain();
      gain.gain().set_value(depth);
      osc.connect(&gain);
      s.push(Node::Osc(osc));
      Ok(s.push(Node::Gain(gain)))
    });
    // Огибающая: усилитель, который в s.at поднимается до peak · velocity за attack, спадает к sustain за decay, а
    // в s.at + s.length затухает за release.
    methods.add_method("env", |_, s, spec: Option<Table>| {
      let get = |key: &str, default: f64| spec.as_ref().and_then(|spec| spec.get::<Option<f64>>(key).ok().flatten()).unwrap_or(default);
      let (attack, decay, sustain, release, peak) = (get("attack", 0.01), get("decay", 0.1), get("sustain", 0.7), get("release", 0.2), get("peak", 1.0) * s.velocity);
      let gain = s.engine().ctx.create_gain();
      let param = gain.gain();
      param.set_value_at_time(0.0, s.at).linear_ramp_to_value_at_time(peak as f32, s.at + attack).linear_ramp_to_value_at_time((peak * sustain) as f32, s.at + attack + decay);
      if let Some(length) = s.length {
        let end = s.at + length.max(attack + decay);
        param.set_value_at_time((peak * sustain) as f32, end).linear_ramp_to_value_at_time(0.0, end + release);
        let mut nodes = s.nodes.borrow_mut();
        nodes.release = nodes.release.max(release);
      }
      Ok(s.push(Node::Gain(gain)))
    });
    // Выход голоса: к нему подключают последний узел — s:out() или a:to(s:out()).
    methods.add_method("out", |_, s, ()| Ok(NodeRef { nodes: s.nodes.clone(), index: s.nodes.borrow().out }));
  }
}

/// Звучание голоса: его узлы и что получит update.
pub struct Sound {
  nodes: Rc<RefCell<Nodes>>,
  pan: StereoPannerNode,
  gain: GainNode,
  handles: Option<RegistryKey>,
  update: Option<Function>,
  /// Звучит, пока не отпустят (не нота и не короткий звук).
  held: bool,
}

impl Sound {
  /// Громкость и панорама — плавно.
  pub fn place(&self, gain: f32, pan: f32) {
    let now = self.nodes.borrow().engine.now();
    self.gain.gain().set_target_at_time(gain, now, 0.05);
    self.pan.pan().set_target_at_time(pan.clamp(-1.0, 1.0), now, 0.05);
  }

  /// Ручки голоса: update(узлы, knobs).
  pub fn knobs(&self, lua: &Lua, knobs: &Table) -> mlua::Result<()> {
    if let (Some(update), Some(handles)) = (&self.update, &self.handles) {
      update.call::<()>((lua.registry_value::<Value>(handles)?, knobs))?;
    }
    Ok(())
  }

  /// Отпустить: затихает за fade секунд и останавливается.
  pub fn release(self, fade: f64) {
    let mut nodes = self.nodes.borrow_mut();
    let now = nodes.engine.now();
    self.gain.gain().cancel_scheduled_values(now).set_value_at_time(self.gain.gain().value(), now).linear_ramp_to_value_at_time(0.0, now + fade);
    for node in &mut nodes.list {
      node.stop(now + fade + 0.05);
    }
    let _ = self.held;
  }
}

/// Синтезатор: голоса по имени (виды раздела `sounds`), запечённые звуки, шины.
pub struct Audio {
  pub engine: Rc<Engine>,
  voices: HashMap<String, Table>,
  baked: HashMap<String, AudioBuffer>,
}

fn noise(ctx: &AudioContext, color: usize) -> AudioBuffer {
  let rate = ctx.sample_rate();
  let length = (rate * 2.0) as usize;
  let mut buffer = ctx.create_buffer(1, length, rate);
  let data = buffer.get_channel_data_mut(0);
  let mut seed: u32 = 0x9e3779b9 ^ color as u32;
  let mut white = || {
    seed ^= seed << 13;
    seed ^= seed >> 17;
    seed ^= seed << 5;
    seed as f32 / u32::MAX as f32 * 2.0 - 1.0
  };
  let (mut b0, mut b1, mut b2, mut last) = (0.0f32, 0.0f32, 0.0f32, 0.0f32);
  for sample in data.iter_mut() {
    let w = white();
    *sample = match color {
      // Розовый — фильтр Пола Келлета (упрощённый), коричневый — накопление с утечкой.
      1 => {
        b0 = 0.99765 * b0 + w * 0.0990460;
        b1 = 0.96300 * b1 + w * 0.2965164;
        b2 = 0.57000 * b2 + w * 1.0526913;
        (b0 + b1 + b2 + w * 0.1848) * 0.18
      }
      2 => {
        last = (last + 0.02 * w) / 1.02;
        last * 3.5
      }
      _ => w * 0.5,
    };
  }
  buffer
}

impl Audio {
  /// Синтезатор или None, если звуку некуда выйти.
  pub fn new() -> Option<Self> {
    let ctx = std::panic::catch_unwind(AudioContext::default).ok()?;
    let master = ctx.create_gain();
    master.gain().set_value(0.8);
    master.connect(&ctx.destination());
    let mut buses = HashMap::new();
    for name in BUSES {
      let bus = ctx.create_gain();
      bus.connect(&master);
      buses.insert(name, bus);
    }
    let noise = [noise(&ctx, 0), noise(&ctx, 1), noise(&ctx, 2)];
    Some(Self { engine: Rc::new(Engine { ctx, master, buses, noise }), voices: HashMap::new(), baked: HashMap::new() })
  }

  pub fn now(&self) -> f64 {
    self.engine.now()
  }

  /// Голоса из раздела `sounds`; запечённые считаются сразу.
  pub fn load(&mut self, lua: &Lua, voices: Vec<(String, Table)>) -> mlua::Result<()> {
    for (name, voice) in voices {
      if let Some(bake) = voice.get::<Option<Table>>("bake")? {
        let render: Function = bake.get("render")?;
        let rate = self.engine.ctx.sample_rate();
        let length = ((bake.get::<Option<f64>>("length")?.unwrap_or(0.5) * rate as f64) as usize).max(1);
        let out = lua.create_buffer(vec![0u8; length * 4])?;
        render.call::<()>((&out, rate, length))?;
        let bytes = out.to_vec();
        let mut buffer = self.engine.ctx.create_buffer(1, length, rate);
        for (sample, chunk) in buffer.get_channel_data_mut(0).iter_mut().zip(bytes.chunks_exact(4)) {
          *sample = f32::from_le_bytes(chunk.try_into().expect("4 байта"));
        }
        self.baked.insert(name.clone(), buffer);
      }
      self.voices.insert(name, voice);
    }
    Ok(())
  }

  /// Звучание голоса voice на шине bus: с at на length секунд (None — пока не отпустят), нота note Гц.
  pub fn start(&self, lua: &Lua, voice: &str, bus: &str, at: Option<f64>, length: Option<f64>, note: f64, velocity: f64) -> mlua::Result<Option<Sound>> {
    let Some(table) = self.voices.get(voice) else { return Ok(None) };
    let engine = self.engine.clone();
    let at = at.unwrap_or_else(|| engine.now());
    let gain = engine.ctx.create_gain();
    let pan = engine.ctx.create_stereo_panner();
    gain.connect(&pan);
    pan.connect(engine.buses.get(bus).unwrap_or(&engine.master));
    // Выход голоса — усилитель громкости звучания; узел 0 в списке.
    let out = engine.ctx.create_gain();
    out.connect(&gain);
    let nodes = Rc::new(RefCell::new(Nodes { list: vec![Node::Gain(out)], release: 0.0, engine: engine.clone(), out: 0 }));
    if let Some(buffer) = self.baked.get(voice) {
      let mut source = engine.ctx.create_buffer_source();
      source.set_buffer(buffer.clone());
      source.playback_rate().set_value((note / 440.0) as f32);
      if let Node::Gain(out) = &nodes.borrow().list[0] {
        out.gain().set_value(velocity as f32);
        source.connect(out);
      }
      source.start_at(at);
      nodes.borrow_mut().list.push(Node::Source(source));
      return Ok(Some(Sound { nodes, pan, gain, handles: None, update: None, held: false }));
    }
    let graph: Function = table.get("graph")?;
    let builder = Builder { nodes: nodes.clone(), at, length, note, velocity };
    let handles: Value = graph.call(builder)?;
    if let Some(length) = length {
      let mut borrowed = nodes.borrow_mut();
      let end = at + length + borrowed.release + 0.05;
      for node in &mut borrowed.list {
        node.stop(end);
      }
    }
    let update = table.get::<Option<Function>>("update")?;
    let handles = if handles.is_nil() { None } else { Some(lua.create_registry_value(handles)?) };
    Ok(Some(Sound { nodes, pan, gain, handles, update, held: length.is_none() }))
  }

  /// Короткий звук сразу: голос со своей длиной (`length` голоса или 0,3 с).
  pub fn play(&self, lua: &Lua, voice: &str, bus: &str, gain: f32, pan: f32, pitch: f64) -> mlua::Result<()> {
    let Some(table) = self.voices.get(voice) else { return Ok(()) };
    let length = table.get::<Option<f64>>("length")?.unwrap_or(0.3);
    let note = table.get::<Option<f64>>("note")?.unwrap_or(440.0) * pitch;
    if let Some(sound) = self.start(lua, voice, bus, None, Some(length), note, 1.0)? {
      sound.gain.gain().set_value(gain);
      sound.pan.pan().set_value(pan.clamp(-1.0, 1.0));
    }
    Ok(())
  }

  pub fn set_volume(&self, bus: &str, volume: f32) {
    let node = if bus == "master" { Some(&self.engine.master) } else { self.engine.buses.get(bus) };
    if let Some(node) = node {
      node.gain().set_target_at_time(volume, self.engine.now(), 0.05);
    }
  }
}
