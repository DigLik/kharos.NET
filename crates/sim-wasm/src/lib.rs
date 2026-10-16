//! Симуляция в wasm для хоста на TS (`packages/sim-rust`): плоские функции без wasm-bindgen, данные — байтами через
//! память модуля, как у `luau.wasm`. Симуляций в одном экземпляре сколько угодно, у каждой свой номер.
//!
//! Строки и JSON TS кладёт в буфер, выделенный `alloc`, и освобождает `dealloc`. Ответ функция пишет в общий буфер
//! ответа и отдаёт его начало; длина — `out_len`. Буфер живёт до следующего ответа.

use kharos_engine::terrain::{GeneratorConfig, LandArea};
use kharos_engine::{layers, Op, Owner};
use kharos_sim::vision::Bounds;
use kharos_sim::Sim;
use serde_json::Value;
use std::cell::RefCell;

#[derive(Default)]
struct State {
  sims: Vec<Option<Sim>>,
  out: Vec<u8>,
}

thread_local! {
  static STATE: RefCell<State> = RefCell::new(State::default());
}

/// Ответ — в общий буфер; отдаёт его начало.
fn answer(state: &mut State, bytes: Vec<u8>) -> *const u8 {
  state.out = bytes;
  state.out.as_ptr()
}

fn with_sim<R>(id: u32, act: impl FnOnce(&mut Sim) -> R) -> Option<R> {
  STATE.with_borrow_mut(|state| state.sims.get_mut(id as usize).and_then(Option::as_mut).map(act))
}

/// Выделяет буфер под данные от TS.
#[no_mangle]
pub extern "C" fn alloc(len: usize) -> *mut u8 {
  let mut buffer = Vec::<u8>::with_capacity(len);
  let ptr = buffer.as_mut_ptr();
  std::mem::forget(buffer);
  ptr
}

/// # Safety
/// ptr и len — от `alloc`.
#[no_mangle]
pub unsafe extern "C" fn dealloc(ptr: *mut u8, len: usize) {
  drop(Vec::from_raw_parts(ptr, 0, len));
}

/// Начало последнего ответа: так его читают функции, что отвечают кодом, а текст оставляют в буфере (`sim_apply`).
#[no_mangle]
pub extern "C" fn out_ptr() -> *const u8 {
  STATE.with_borrow(|state| state.out.as_ptr())
}

/// Длина последнего ответа.
#[no_mangle]
pub extern "C" fn out_len() -> usize {
  STATE.with_borrow(|state| state.out.len())
}

/// Новая симуляция на земле из генератора (JSON `GeneratorConfig` в буфере от `alloc`) с картой мира — областью
/// чанков left, top, width, height (`LandArea`), границами карты в тайлах и туманом (fog ≠ 0); отдаёт её номер, -1 — настройки не читаются (ответ — текст ошибки).
/// Журнал своих операций она ведёт сразу.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn sim_new(ptr: *const u8, len: usize, left: i32, top: i32, width: i32, height: i32, bounds_left: i32, bounds_top: i32, bounds_right: i32, bounds_bottom: i32, fog: i32) -> i32 {
  let config = match serde_json::from_slice::<GeneratorConfig>(std::slice::from_raw_parts(ptr, len)) {
    Ok(config) => config,
    Err(error) => {
      return STATE.with_borrow_mut(|state| {
        answer(state, error.to_string().into_bytes());
        -1
      })
    }
  };
  STATE.with_borrow_mut(|state| {
    let bounds = Bounds { left: bounds_left, top: bounds_top, right: bounds_right, bottom: bounds_bottom };
    let mut sim = Sim::new(config, LandArea { left, top, width, height }, bounds, fog != 0);
    sim.world.record(true);
    match state.sims.iter().position(Option::is_none) {
      Some(at) => {
        state.sims[at] = Some(sim);
        at as i32
      }
      None => {
        state.sims.push(Some(sim));
        state.sims.len() as i32 - 1
      }
    }
  })
}

#[no_mangle]
pub extern "C" fn sim_free(id: u32) {
  STATE.with_borrow_mut(|state| {
    if let Some(slot) = state.sims.get_mut(id as usize) {
      *slot = None;
    }
  });
}

/// Применяет журнал TS (JSON-список операций). 0 — применён; иначе ответ — текст ошибки.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
pub unsafe extern "C" fn sim_apply(id: u32, ptr: *const u8, len: usize) -> i32 {
  let bytes = std::slice::from_raw_parts(ptr, len);
  let result = with_sim(id, |sim| -> Result<(), String> {
    let ops: Value = serde_json::from_slice(bytes).map_err(|error| error.to_string())?;
    for op in ops.as_array().ok_or("журнал — не список")? {
      sim.world.replay(Op::from_json(op)?);
    }
    sim.settle();
    Ok(())
  });
  code(result)
}

/// Код ответа: 0 — сделано, 1 — ошибка (ответ — её текст), 2 — нет симуляции.
fn code(result: Option<Result<(), String>>) -> i32 {
  match result {
    Some(Ok(())) => 0,
    Some(Err(error)) => STATE.with_borrow_mut(|state| {
      answer(state, error.into_bytes());
      1
    }),
    None => 2,
  }
}

/// Свои операции симуляции с прошлого раза — JSON-списком, в форме журнала TS.
#[no_mangle]
pub extern "C" fn sim_journal(id: u32) -> *const u8 {
  let json = with_sim(id, |sim| Value::from(sim.world.take_journal().iter().map(Op::to_json).collect::<Vec<_>>()).to_string()).unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, json.into_bytes()))
}

/// Снимок мира — JSON в форме `WorldSnapshot` TS.
#[no_mangle]
pub extern "C" fn sim_snapshot(id: u32) -> *const u8 {
  let json = with_sim(id, |sim| sim.world.snapshot().to_string()).unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, json.into_bytes()))
}

/// Порядок сущностей в хранилище компонента (ключ — UTF-8 в буфере от `alloc`): JSON-список номеров. По нему проверка
/// сверяет, что Rust обойдёт хранилище так же, как TS.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
pub unsafe extern "C" fn sim_order(id: u32, ptr: *const u8, len: usize) -> *const u8 {
  let key = std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).unwrap_or_default();
  let json = with_sim(id, |sim| Value::from(sim.world.query(&[key])).to_string()).unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, json.into_bytes()))
}

/// Системы, которые уже на Rust: JSON-список `{ name, reads }` — имя в порядке тика и что она читает.
#[no_mangle]
pub extern "C" fn sim_systems() -> *const u8 {
  let list: Vec<Value> = kharos_sim::SYSTEMS.iter().map(|system| serde_json::json!({ "name": system.name, "reads": system.reads })).collect();
  STATE.with_borrow_mut(|state| answer(state, Value::from(list).to_string().into_bytes()))
}

/// Выполняет систему на Rust (имя — UTF-8 в буфере от `alloc`) в тике tick длиной step секунд. 0 — выполнена,
/// 1 — такой на Rust нет, 2 — нет симуляции.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
pub unsafe extern "C" fn sim_run(id: u32, ptr: *const u8, len: usize, tick: f64, step: f64) -> i32 {
  let name = std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).unwrap_or_default();
  match with_sim(id, |sim| kharos_sim::run(sim, name, kharos_sim::Time { tick: tick as u64, step })) {
    Some(true) => 0,
    Some(false) => 1,
    None => 2,
  }
}

/// Применяет изменения слоёв от TS — буфер в формате `layers::encode` (docs/layers.md): правки карты, загруженный
/// мир. Коды — как у `sim_apply`.
///
/// # Safety
/// ptr и len — буфер от `alloc`.
#[no_mangle]
pub unsafe extern "C" fn sim_layers(id: u32, ptr: *const u8, len: usize) -> i32 {
  let bytes = std::slice::from_raw_parts(ptr, len);
  code(with_sim(id, |sim| sim.apply(&layers::decode(bytes)?)))
}

/// Все блоки слоя (имя — UTF-8 в буфере от `alloc`) владельца owner (-1 — мир) — в формате `layers::encode`; пусто —
/// нет симуляции или слоя. Для сверки зеркала слоёв.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
pub unsafe extern "C" fn sim_layer(id: u32, ptr: *const u8, len: usize, owner: i32) -> *const u8 {
  let name = std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).unwrap_or_default();
  let bytes = with_sim(id, |sim| sim.layers.id(name).map(|layer| layers::encode(&sim.layers.blocks(layer, Owner::from_code(owner as i64))))).flatten().unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, bytes))
}

/// Чанк земли симуляции (в чанках) с выводимым — 4096 байт, как `getChunk` в TS; пусто — нет симуляции. Для сверки.
#[no_mangle]
pub extern "C" fn sim_chunk(id: u32, chunk_x: i32, chunk_y: i32) -> *const u8 {
  let bytes = with_sim(id, |sim| sim.land.chunk(chunk_x, chunk_y).to_vec()).unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, bytes))
}

/// Земля из генератора на Rust: все чанки карты — область тайлов [left, right) × [top, bottom) — подряд, построчно, по
/// 4096 байт, с выводимым. Настройки генератора — JSON `GeneratorConfig` в буфере от `alloc`. Для сверки с TS.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
pub unsafe extern "C" fn land_area(ptr: *const u8, len: usize, left: i32, top: i32, right: i32, bottom: i32) -> *const u8 {
  use kharos_engine::terrain::Land;
  let bytes = std::slice::from_raw_parts(ptr, len);
  let out = match serde_json::from_slice::<GeneratorConfig>(bytes) {
    Ok(config) => {
      let area = LandArea::of(left, top, right, bottom);
      let mut land = Land::new(config, area);
      let mut out = Vec::new();
      for y in 0..area.height {
        for x in 0..area.width {
          out.extend_from_slice(land.chunk(area.left + x, area.top + y));
        }
      }
      out
    }
    Err(_) => Vec::new(),
  };
  STATE.with_borrow_mut(|state| answer(state, out))
}

// Обзор (crates/sim/src/vision.rs) — для TS-обёртки Vision в packages/sim-rust.

/// Ответ числами little-endian.
fn answer_numbers<T: Copy>(values: &[T], bytes: impl Fn(T) -> Vec<u8>) -> *const u8 {
  let out = values.iter().flat_map(|&value| bytes(value)).collect();
  STATE.with_borrow_mut(|state| answer(state, out))
}

/// Заводит игроку обзор и считает его на мире этого мига, если его ещё нет (первый вопрос в TS).
#[no_mangle]
pub extern "C" fn vision_open(id: u32, player: u32, tick: f64) {
  with_sim(id, |sim| sim.vision.open(&sim.world, &mut sim.layers, player, tick as i64, false));
}

/// Видит ли игрок тайл сейчас: 1 — да.
#[no_mangle]
pub extern "C" fn vision_sees(id: u32, player: u32, x: f64, y: f64) -> i32 {
  with_sim(id, |sim| sim.vision.sees(&sim.layers, player, x, y)).unwrap_or(false) as i32
}

/// Бывал ли тайл в виду игрока: 1 — да.
#[no_mangle]
pub extern "C" fn vision_explored(id: u32, player: u32, x: f64, y: f64) -> i32 {
  with_sim(id, |sim| sim.vision.explored(&sim.layers, player, x, y)).unwrap_or(false) as i32
}

/// Тайлы карты построчно: 0 — не разведан, 1 — разведан, 2 — виден.
#[no_mangle]
pub extern "C" fn vision_cells(id: u32, player: u32) -> *const u8 {
  let cells = with_sim(id, |sim| sim.vision.cells(&sim.layers, player)).unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, cells))
}

/// Сколько раз менялось открытое игроком.
#[no_mangle]
pub extern "C" fn vision_changed(id: u32, player: u32) -> u32 {
  with_sim(id, |sim| sim.vision.changed(player)).unwrap_or(0)
}

/// Ячейки стороной size, где игрок видит хоть тайл: ключи tileKey, f64.
#[no_mangle]
pub extern "C" fn vision_seen(id: u32, player: u32, size: i32) -> *const u8 {
  let keys = with_sim(id, |sim| sim.vision.seen_cells(player, size)).unwrap_or_default();
  answer_numbers(&keys, |key| key.to_le_bytes().to_vec())
}

/// Разведанное отрезками: u32.
#[no_mangle]
pub extern "C" fn vision_map(id: u32, player: u32) -> *const u8 {
  let runs = with_sim(id, |sim| sim.vision.map(&sim.layers, player)).unwrap_or_default();
  answer_numbers(&runs, |run| run.to_le_bytes().to_vec())
}

/// Отмечает разведанным отрезки map (u32 little-endian в буфере от `alloc`).
///
/// # Safety
/// ptr и len — буфер от `alloc`.
#[no_mangle]
pub unsafe extern "C" fn vision_explore(id: u32, player: u32, ptr: *const u8, len: usize) {
  let runs: Vec<u32> = std::slice::from_raw_parts(ptr, len).chunks_exact(4).map(|bytes| u32::from_le_bytes(bytes.try_into().unwrap())).collect();
  with_sim(id, |sim| sim.vision.explore(&mut sim.layers, player, &runs));
}

/// Игроки с обзором в порядке появления: u32.
#[no_mangle]
pub extern "C" fn vision_players(id: u32) -> *const u8 {
  let players = with_sim(id, |sim| sim.vision.players().to_vec()).unwrap_or_default();
  answer_numbers(&players, |player| player.to_le_bytes().to_vec())
}

#[no_mangle]
pub extern "C" fn vision_forget(id: u32, player: u32) {
  with_sim(id, |sim| sim.vision.forget(&mut sim.layers, player));
}

#[no_mangle]
pub extern "C" fn vision_reset(id: u32) {
  with_sim(id, |sim| sim.vision.reset(&mut sim.layers));
}

// Память слоёв (crates/sim/src/recall.rs): 0 — земля, 1 — покрытие.

fn memory(sim: &mut Sim, layer: f64) -> &mut kharos_sim::recall::Recall {
  if layer == 0.0 {
    &mut sim.land_memory
  } else {
    &mut sim.pave_memory
  }
}

/// Досылает памяти слоёв то, что изменилось в TS: записи по шесть f64 — операция, слой, игрок, x, y, значение.
/// Операции: 0 — тайл слоя изменился, 1 — исходный тайл земли (значение — хранимые байты одним числом), 2 — тайл
/// земли забыт (правка редактора), 3 — знание игрока из сохранения.
///
/// # Safety
/// ptr и len — буфер от `alloc`.
#[no_mangle]
pub unsafe extern "C" fn memory_input(id: u32, ptr: *const u8, len: usize) {
  let values: Vec<f64> = std::slice::from_raw_parts(ptr, len).chunks_exact(8).map(|bytes| f64::from_le_bytes(bytes.try_into().unwrap())).collect();
  with_sim(id, |sim| {
    for record in values.chunks_exact(6) {
      let [op, layer, player, x, y, value] = record.try_into().unwrap();
      let (memory, tile) = (memory(sim, layer), (x as i32, y as i32));
      match op as i32 {
        0 => memory.change(tile),
        1 => {
          memory.original.insert(tile, value as u32);
        }
        2 => memory.forget(tile),
        _ => memory.know(player as u32, tile, value as u32),
      }
    }
  });
}

/// Что изменилось в памяти слоёв с прошлого вызова: записи по шесть f64 — слой, событие, игрок, x, y, значение.
/// События: 0 — игроку заведена память, 1 — игрок узнал значение тайла, 2 — игрок узнал, что тайл как у всех.
#[no_mangle]
pub extern "C" fn memory_take(id: u32) -> *const u8 {
  use kharos_sim::recall::Event;
  let values = with_sim(id, |sim| {
    let mut values = Vec::new();
    for layer in [0.0, 1.0] {
      for event in memory(sim, layer).take_events() {
        values.extend_from_slice(&match event {
          Event::Track { player } => [layer, 0.0, player as f64, 0.0, 0.0, 0.0],
          Event::Learn { player, tile, value: Some(value) } => [layer, 1.0, player as f64, tile.0 as f64, tile.1 as f64, value as f64],
          Event::Learn { player, tile, value: None } => [layer, 2.0, player as f64, tile.0 as f64, tile.1 as f64, 0.0],
        });
      }
    }
    values
  })
  .unwrap_or_default();
  answer_numbers(&values, |value| value.to_le_bytes().to_vec())
}

// Память о зданиях (crates/sim/src/build_memory.rs).

/// Досылает памяти о зданиях то, что изменилось в TS: JSON-массив операций — `["know", игрок, сущность, x, y, вид]`
/// (из сохранения) и `["forget", игрок]` (игрок начинает заново). Коды — как у `sim_apply`.
///
/// # Safety
/// ptr и len — буфер от `alloc` с UTF-8 внутри.
#[no_mangle]
pub unsafe extern "C" fn build_memory_input(id: u32, ptr: *const u8, len: usize) -> i32 {
  use kharos_sim::build_memory::Ghost;
  let bytes = std::slice::from_raw_parts(ptr, len);
  code(with_sim(id, |sim| {
    let ops: Vec<Value> = serde_json::from_slice(bytes).map_err(|error| error.to_string())?;
    for op in ops {
      let field = |at: usize| op.get(at).and_then(Value::as_f64).unwrap_or(0.0);
      match op.get(0).and_then(Value::as_str) {
        Some("know") => sim.build_memory.know(field(1) as u32, field(2) as u32, Ghost { x: field(3), y: field(4), kind: op.get(5).and_then(Value::as_str).unwrap_or_default().to_string() }),
        Some("forget") => sim.build_memory.forget_player(field(1) as u32),
        _ => return Err(format!("память о зданиях: неизвестная операция {op}")),
      }
    }
    Ok(())
  }))
}

/// Что изменилось в памяти о зданиях с прошлого вызова: JSON `[[игрок, сущность, 1 — запомнил | 0 — забыл], …]`.
#[no_mangle]
pub extern "C" fn build_memory_take(id: u32) -> *const u8 {
  use kharos_sim::build_memory::Event;
  let events = with_sim(id, |sim| sim.build_memory.take_events()).unwrap_or_default();
  let out: Vec<[u32; 3]> = events
    .into_iter()
    .map(|event| match event {
      Event::Remember { player, entity } => [player, entity, 1],
      Event::Forget { player, entity } => [player, entity, 0],
    })
    .collect();
  STATE.with_borrow_mut(|state| answer(state, serde_json::to_vec(&out).unwrap()))
}
