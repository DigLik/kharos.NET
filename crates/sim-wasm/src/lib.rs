//! Симуляция в wasm для хоста на TS (`packages/sim-rust`): плоские функции без wasm-bindgen, данные — байтами через
//! память модуля, как у `luau.wasm`. Симуляций в одном экземпляре сколько угодно, у каждой свой номер.
//!
//! Строки и JSON TS кладёт в буфер, выделенный `alloc`, и освобождает `dealloc`. Ответ функция пишет в общий буфер
//! ответа и отдаёт его начало; длина — `out_len`. Буфер живёт до следующего ответа.

use kharos_engine::{Op, World};
use serde_json::Value;
use std::cell::RefCell;

#[derive(Default)]
struct State {
  sims: Vec<Option<World>>,
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

fn with_sim<R>(id: u32, act: impl FnOnce(&mut World) -> R) -> Option<R> {
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

/// Новая симуляция; отдаёт её номер. Журнал своих операций она ведёт сразу.
#[no_mangle]
pub extern "C" fn sim_new() -> u32 {
  STATE.with_borrow_mut(|state| {
    let mut world = World::new();
    world.record(true);
    let free = state.sims.iter().position(Option::is_none);
    match free {
      Some(at) => {
        state.sims[at] = Some(world);
        at as u32
      }
      None => {
        state.sims.push(Some(world));
        state.sims.len() as u32 - 1
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
  let result = with_sim(id, |world| -> Result<(), String> {
    let ops: Value = serde_json::from_slice(bytes).map_err(|error| error.to_string())?;
    for op in ops.as_array().ok_or("журнал — не список")? {
      world.replay(Op::from_json(op)?);
    }
    Ok(())
  });
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
  let json = with_sim(id, |world| Value::from(world.take_journal().iter().map(Op::to_json).collect::<Vec<_>>()).to_string()).unwrap_or_default();
  STATE.with_borrow_mut(|state| answer(state, json.into_bytes()))
}

/// Снимок мира — JSON в форме `WorldSnapshot` TS.
#[no_mangle]
pub extern "C" fn sim_snapshot(id: u32) -> *const u8 {
  let json = with_sim(id, |world| world.snapshot().to_string()).unwrap_or_default();
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
  let json = with_sim(id, |world| Value::from(world.query(&[key])).to_string()).unwrap_or_default();
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
  match with_sim(id, |world| kharos_sim::run(world, name, kharos_sim::Time { tick: tick as u64, step })) {
    Some(true) => 0,
    Some(false) => 1,
    None => 2,
  }
}
