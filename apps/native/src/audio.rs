//! Привязки звука — Web Audio API. Граф звука, расписание параметров и вывод на устройство даёт крейт
//! `web-audio-api` (он написан по спецификации и выводит звук через `cpal`), а здесь — только перевод вызовов игры.
//!
//! Устройство то же, что у холста и GL: игра видит контексты, узлы и буферы числами, а Rust держит за ними настоящие
//! объекты. Обёртка (`js/audio.js`) ведёт браузерную сторону — классы узлов, их параметры и события окончания.
//! Узел, который игра отпустила (его собрал сборщик мусора), здесь тоже отпускается: граф крейта сам держит его,
//! пока тот звучит, и освобождает, когда звучать больше нечему.
//!
//! Крейт на ошибки в аргументах паникует там, где браузер бросает исключение (второй `start`, буфер чужой частоты у
//! свёртки). Паника из операции уронила бы V8, поэтому каждая операция ловит её и отдаёт игре исключением.

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Mutex, Once};

use deno_core::op2;
use deno_error::JsErrorBox;
use web_audio_api::context::{AudioContext, AudioContextOptions, AudioContextState, BaseAudioContext};
use web_audio_api::node::{AudioBufferSourceNode, AudioDestinationNode, AudioNode, AudioScheduledSourceNode, BiquadFilterNode, BiquadFilterType, ConvolverNode, DynamicsCompressorNode, GainNode, OscillatorNode, OscillatorType, StereoPannerNode};
use web_audio_api::{AudioBuffer, AudioBufferOptions, AudioParam};

/// Узел графа. Виды — те, что берёт звук игры (`packages/client/src/audio`); номер вида тот же, что у обёртки (`KIND` в `js/audio.js`).
enum Node {
  Destination(AudioDestinationNode),
  Gain(GainNode),
  Oscillator(OscillatorNode),
  Source(AudioBufferSourceNode),
  Biquad(BiquadFilterNode),
  Panner(StereoPannerNode),
  Convolver(ConvolverNode),
  Compressor(DynamicsCompressorNode),
}

impl Node {
  fn node(&self) -> &dyn AudioNode {
    match self {
      Node::Destination(node) => node,
      Node::Gain(node) => node,
      Node::Oscillator(node) => node,
      Node::Source(node) => node,
      Node::Biquad(node) => node,
      Node::Panner(node) => node,
      Node::Convolver(node) => node,
      Node::Compressor(node) => node,
    }
  }

  /// Параметр узла по номеру имени (`PARAM` в `js/audio.js`): 0 — gain, 1 — frequency, 2 — detune, 3 — Q,
  /// 4 — playbackRate, 5 — pan, 6 — threshold, 7 — ratio, 8 — knee, 9 — attack, 10 — release.
  fn param(&self, which: u32) -> Option<&AudioParam> {
    Some(match (self, which) {
      (Node::Gain(node), 0) => node.gain(),
      (Node::Oscillator(node), 1) => node.frequency(),
      (Node::Oscillator(node), 2) => node.detune(),
      (Node::Source(node), 2) => node.detune(),
      (Node::Source(node), 4) => node.playback_rate(),
      (Node::Biquad(node), 0) => node.gain(),
      (Node::Biquad(node), 1) => node.frequency(),
      (Node::Biquad(node), 2) => node.detune(),
      (Node::Biquad(node), 3) => node.q(),
      (Node::Panner(node), 5) => node.pan(),
      (Node::Compressor(node), 6) => node.threshold(),
      (Node::Compressor(node), 7) => node.ratio(),
      (Node::Compressor(node), 8) => node.knee(),
      (Node::Compressor(node), 9) => node.attack(),
      (Node::Compressor(node), 10) => node.release(),
      _ => return None,
    })
  }
}

thread_local! {
  /// Контексты звука: у каждого свой поток вывода на устройство.
  static CONTEXTS: RefCell<HashMap<u32, AudioContext>> = RefCell::new(HashMap::new());
  /// Узлы графа всех контекстов.
  static NODES: RefCell<HashMap<u32, Node>> = RefCell::new(HashMap::new());
  /// Записи звука: не привязаны к контексту, как и `AudioBuffer` у страницы.
  static BUFFERS: RefCell<HashMap<u32, AudioBuffer>> = RefCell::new(HashMap::new());
  /// Следующий номер. Номера не переиспользуются: старый номер даст «ничего», а не чужое.
  static NEXT: Cell<u32> = const { Cell::new(1) };
  /// Идёт работа под `guard`: её паника станет исключением JS, и печатать её в журнал незачем.
  static GUARDED: Cell<bool> = const { Cell::new(false) };
}

/// Источники, которые доиграли (`onended`): крейт сообщает об этом со своего потока событий, а обёртка забирает
/// номера раз в кадр (см. `op_audio_ended`).
static ENDED: Mutex<Vec<u32>> = Mutex::new(Vec::new());

fn next() -> u32 {
  NEXT.with(|next| {
    let id = next.get();
    next.set(id + 1);
    id
  })
}

/// Работа, которая может паниковать в крейте: паника становится исключением JS с текстом крейта. Обычную печать
/// паники такая работа обходит: текст и так уходит игре.
fn guard<R>(what: &str, f: impl FnOnce() -> R) -> Result<R, JsErrorBox> {
  static HOOK: Once = Once::new();
  HOOK.call_once(|| {
    let print = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
      if !GUARDED.with(Cell::get) {
        print(info)
      }
    }));
  });
  GUARDED.with(|guarded| guarded.set(true));
  let result = catch_unwind(AssertUnwindSafe(f));
  GUARDED.with(|guarded| guarded.set(false));
  result.map_err(|panic| {
    let message = panic.downcast_ref::<String>().map(String::as_str).or_else(|| panic.downcast_ref::<&str>().copied()).unwrap_or("ошибка звука");
    JsErrorBox::generic(format!("{what}: {message}"))
  })
}

fn with_node<R>(id: u32, f: impl FnOnce(&mut Node) -> R) -> Option<R> {
  NODES.with(|nodes| nodes.borrow_mut().get_mut(&id).map(f))
}

/// Заводит контекст звука на частоте rate (0 — частота устройства) и отдаёт его номер. Нет устройства — 0: игра
/// тогда идёт без звука, как страница, у которой браузер звук не дал.
#[op2(fast)]
pub fn op_audio_context(rate: f64) -> u32 {
  let options = AudioContextOptions { sample_rate: (rate > 0.0).then_some(rate as f32), ..Default::default() };
  match catch_unwind(|| AudioContext::try_new(options).map_err(|error| error.to_string())) {
    Ok(Ok(context)) => {
      println!("звук: контекст {} Гц", context.sample_rate());
      let id = next();
      CONTEXTS.with(|contexts| contexts.borrow_mut().insert(id, context));
      id
    }
    Ok(Err(error)) => {
      eprintln!("звук не заведён: {error}");
      0
    }
    Err(_) => {
      eprintln!("звук не заведён: крейт упал при создании контекста");
      0
    }
  }
}

/// Время контекста в секундах — `currentTime`.
#[op2(fast)]
pub fn op_audio_time(context: u32) -> f64 {
  CONTEXTS.with(|contexts| contexts.borrow().get(&context).map_or(0.0, |context| context.current_time()))
}

/// Частота контекста — `sampleRate`.
#[op2(fast)]
pub fn op_audio_rate(context: u32) -> f64 {
  CONTEXTS.with(|contexts| contexts.borrow().get(&context).map_or(0.0, |context| context.sample_rate() as f64))
}

/// Состояние контекста: 0 — приостановлен, 1 — играет, 2 — закрыт (или его нет).
#[op2(fast)]
pub fn op_audio_state(context: u32) -> u32 {
  CONTEXTS.with(|contexts| {
    contexts.borrow().get(&context).map_or(2, |context| match context.state() {
      AudioContextState::Suspended => 0,
      AudioContextState::Running => 1,
      AudioContextState::Closed => 2,
    })
  })
}

/// Смена состояния: 0 — `resume`, 1 — `suspend`, 2 — `close`. Закрытый контекст отпускает устройство и уходит из реестра.
#[op2(fast)]
pub fn op_audio_control(context: u32, action: u32) -> Result<(), JsErrorBox> {
  if action == 2 {
    let Some(closing) = CONTEXTS.with(|contexts| contexts.borrow_mut().remove(&context)) else { return Ok(()) };
    return guard("close", || closing.close_sync());
  }
  CONTEXTS.with(|contexts| {
    let contexts = contexts.borrow();
    let Some(context) = contexts.get(&context) else { return Ok(()) };
    guard(if action == 0 { "resume" } else { "suspend" }, || if action == 0 { context.resume_sync() } else { context.suspend_sync() })
  })
}

/// Заводит узел вида kind (`KIND` в `js/audio.js`) в контексте и отдаёт его номер. 0 — контекста нет.
#[op2(fast)]
pub fn op_audio_node(context: u32, kind: u32) -> Result<u32, JsErrorBox> {
  let node = CONTEXTS.with(|contexts| {
    let contexts = contexts.borrow();
    let Some(context) = contexts.get(&context) else { return Ok(None) };
    guard("создание узла", || match kind {
      0 => Some(Node::Destination(context.destination())),
      1 => Some(Node::Gain(context.create_gain())),
      2 => Some(Node::Oscillator(context.create_oscillator())),
      3 => Some(Node::Source(context.create_buffer_source())),
      4 => Some(Node::Biquad(context.create_biquad_filter())),
      5 => Some(Node::Panner(context.create_stereo_panner())),
      6 => Some(Node::Convolver(context.create_convolver())),
      7 => Some(Node::Compressor(context.create_dynamics_compressor())),
      _ => None,
    })
  })?;
  let Some(node) = node else { return Ok(0) };
  let id = next();
  // Конец звучания источника — в общую очередь: обёртка позовёт `onended` в своём кадре.
  match &node {
    Node::Oscillator(source) => source.set_onended(move |_| ENDED.lock().unwrap().push(id)),
    Node::Source(source) => source.set_onended(move |_| ENDED.lock().unwrap().push(id)),
    _ => {}
  }
  NODES.with(|nodes| nodes.borrow_mut().insert(id, node));
  Ok(id)
}

/// Игра отпустила узел. Звучать он продолжит, пока ему есть что играть: его держит граф крейта.
#[op2(fast)]
pub fn op_audio_drop(node: u32) {
  if let Some(node) = NODES.with(|nodes| nodes.borrow_mut().remove(&node)) {
    // Отписка от конца звучания: иначе крейт держал бы колбэк, а номер пришёл бы уже без узла.
    match &node {
      Node::Oscillator(source) => source.clear_onended(),
      Node::Source(source) => source.clear_onended(),
      _ => {}
    }
  }
}

/// Соединение: выход output узла from — во вход input узла to, а с param ≥ 0 — в его параметр (тогда input не важен).
#[op2(fast)]
pub fn op_audio_connect(from: u32, to: u32, param: i32, output: u32, input: u32) -> Result<(), JsErrorBox> {
  NODES.with(|nodes| {
    let nodes = nodes.borrow();
    let (Some(source), Some(target)) = (nodes.get(&from), nodes.get(&to)) else { return Ok(()) };
    let source = source.node();
    guard("connect", || {
      if param >= 0 {
        if let Some(param) = target.param(param as u32) {
          source.connect_from_output_to_input(param, output as usize, 0);
        }
      } else {
        source.connect_from_output_to_input(target.node(), output as usize, input as usize);
      }
    })
  })
}

/// Разъединение: to = 0 — со всеми, иначе — с узлом to или (param ≥ 0) с его параметром.
#[op2(fast)]
pub fn op_audio_disconnect(from: u32, to: u32, param: i32) -> Result<(), JsErrorBox> {
  NODES.with(|nodes| {
    let nodes = nodes.borrow();
    let Some(source) = nodes.get(&from) else { return Ok(()) };
    let source = source.node();
    guard("disconnect", || {
      if to == 0 {
        return source.disconnect();
      }
      let Some(target) = nodes.get(&to) else { return };
      if param >= 0 {
        if let Some(param) = target.param(param as u32) {
          source.disconnect_dest(param);
        }
      } else {
        source.disconnect_dest(target.node());
      }
    })
  })
}

/// Расписание параметра: method 0 — `value =`, 1 — `setValueAtTime`, 2 — `linearRampToValueAtTime`,
/// 3 — `exponentialRampToValueAtTime`, 4 — `setTargetAtTime` (extra — постоянная времени), 5 — `cancelScheduledValues`,
/// 6 — `cancelAndHoldAtTime` (у двух последних время — в value).
#[op2(fast)]
pub fn op_audio_param(node: u32, param: u32, method: u32, value: f64, time: f64, extra: f64) -> Result<(), JsErrorBox> {
  NODES.with(|nodes| {
    let nodes = nodes.borrow();
    let Some(param) = nodes.get(&node).and_then(|node| node.param(param)) else { return Ok(()) };
    let value32 = value as f32;
    guard("AudioParam", || {
      match method {
        0 => param.set_value(value32),
        1 => param.set_value_at_time(value32, time),
        2 => param.linear_ramp_to_value_at_time(value32, time),
        3 => param.exponential_ramp_to_value_at_time(value32, time),
        4 => param.set_target_at_time(value32, time, extra),
        5 => param.cancel_scheduled_values(value),
        _ => param.cancel_and_hold_at_time(value),
      };
    })
  })
}

/// Текущее значение параметра — `value` на чтение.
#[op2(fast)]
pub fn op_audio_param_value(node: u32, param: u32) -> f64 {
  NODES.with(|nodes| nodes.borrow().get(&node).and_then(|node| node.param(param)).map_or(0.0, |param| param.value() as f64))
}

/// Свойство узла: prop 0 — `type` (номер по порядку видов у обёртки), 1 — `loop`, 2 — `loopStart`, 3 — `loopEnd`,
/// 4 — `normalize`.
#[op2(fast)]
pub fn op_audio_set(node: u32, prop: u32, value: f64) -> Result<(), JsErrorBox> {
  with_node(node, |node| {
    guard("свойство узла", || match (node, prop) {
      (Node::Biquad(node), 0) => node.set_type(match value as u32 {
        0 => BiquadFilterType::Lowpass,
        1 => BiquadFilterType::Highpass,
        2 => BiquadFilterType::Bandpass,
        3 => BiquadFilterType::Lowshelf,
        4 => BiquadFilterType::Highshelf,
        5 => BiquadFilterType::Peaking,
        6 => BiquadFilterType::Notch,
        _ => BiquadFilterType::Allpass,
      }),
      (Node::Oscillator(node), 0) => node.set_type(match value as u32 {
        0 => OscillatorType::Sine,
        1 => OscillatorType::Square,
        2 => OscillatorType::Sawtooth,
        _ => OscillatorType::Triangle,
      }),
      (Node::Source(node), 1) => node.set_loop(value != 0.0),
      (Node::Source(node), 2) => node.set_loop_start(value),
      (Node::Source(node), 3) => node.set_loop_end(value),
      (Node::Convolver(node), 4) => node.set_normalize(value != 0.0),
      _ => {}
    })
  })
  .unwrap_or(Ok(()))
}

/// Запись для источника или свёртки — `buffer =`.
#[op2(fast)]
pub fn op_audio_set_buffer(node: u32, buffer: u32) -> Result<(), JsErrorBox> {
  let Some(buffer) = BUFFERS.with(|buffers| buffers.borrow().get(&buffer).cloned()) else { return Ok(()) };
  with_node(node, |node| {
    guard("buffer", || match node {
      Node::Source(node) => node.set_buffer(buffer),
      Node::Convolver(node) => node.set_buffer(buffer),
      _ => {}
    })
  })
  .unwrap_or(Ok(()))
}

/// Запуск источника: `start(when, offset, duration)`; duration < 0 — до конца записи (или без конца у петли).
#[op2(fast)]
pub fn op_audio_start(node: u32, when: f64, offset: f64, duration: f64) -> Result<(), JsErrorBox> {
  with_node(node, |node| {
    guard("start", || match node {
      Node::Oscillator(node) => node.start_at(when),
      Node::Source(node) if duration >= 0.0 => node.start_at_with_offset_and_duration(when, offset, duration),
      Node::Source(node) => node.start_at_with_offset(when, offset),
      _ => {}
    })
  })
  .unwrap_or(Ok(()))
}

/// Остановка источника: `stop(when)`.
#[op2(fast)]
pub fn op_audio_stop(node: u32, when: f64) -> Result<(), JsErrorBox> {
  with_node(node, |node| {
    guard("stop", || match node {
      Node::Oscillator(node) => node.stop_at(when),
      Node::Source(node) => node.stop_at(when),
      _ => {}
    })
  })
  .unwrap_or(Ok(()))
}

/// Источники, доигравшие с прошлого раза.
#[op2]
#[serde]
pub fn op_audio_ended() -> Vec<u32> {
  std::mem::take(&mut *ENDED.lock().unwrap())
}

/// Заводит пустую запись: channels каналов по length отсчётов на частоте rate.
#[op2(fast)]
pub fn op_audio_buffer(channels: u32, length: u32, rate: f64) -> Result<u32, JsErrorBox> {
  let options = AudioBufferOptions { number_of_channels: channels as usize, length: length as usize, sample_rate: rate as f32 };
  let buffer = guard("AudioBuffer", || AudioBuffer::new(options))?;
  let id = next();
  BUFFERS.with(|buffers| buffers.borrow_mut().insert(id, buffer));
  Ok(id)
}

/// Отсчёты в канал записи — `copyToChannel`. Данные — байты `Float32Array` игры.
#[op2(fast)]
pub fn op_audio_buffer_write(buffer: u32, #[buffer] data: &[u8], channel: u32, offset: u32) -> Result<(), JsErrorBox> {
  let samples: Vec<f32> = data.chunks_exact(4).map(|chunk| f32::from_le_bytes(chunk.try_into().unwrap())).collect();
  BUFFERS.with(|buffers| {
    let mut buffers = buffers.borrow_mut();
    let Some(buffer) = buffers.get_mut(&buffer) else { return Ok(()) };
    guard("copyToChannel", || buffer.copy_to_channel_with_offset(&samples, channel as usize, offset as usize))
  })
}

/// Запись больше не нужна: её собрал сборщик мусора. Узлы, которым её уже отдали, держат свою копию.
#[op2(fast)]
pub fn op_audio_buffer_drop(buffer: u32) {
  BUFFERS.with(|buffers| buffers.borrow_mut().remove(&buffer));
}

deno_core::extension!(kharos_audio, ops = [op_audio_context, op_audio_time, op_audio_rate, op_audio_state, op_audio_control, op_audio_node, op_audio_drop, op_audio_connect, op_audio_disconnect, op_audio_param, op_audio_param_value, op_audio_set, op_audio_set_buffer, op_audio_start, op_audio_stop, op_audio_ended, op_audio_buffer, op_audio_buffer_write, op_audio_buffer_drop,],);
