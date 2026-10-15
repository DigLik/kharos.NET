//! Привязки двумерного холста — Canvas 2D API. Им игра рисует картинки кодом (скины, значки, портреты, мини-карту), а
//! интерфейс рисует себя целиком (пункт 10.4 плана): в браузере это родной холст, здесь — `tiny-skia` и свой текст.
//!
//! Устройство то же, что у привязок GL: игра видит холст числом, а Rust держит за ним пиксели. Обёртка
//! (`js/prelude.js`) ведёт браузерную сторону — цвета, пути, состояние и его стек (`save`/`restore`), — и присылает с
//! каждым вызовом то, что нужно для рисования. Обмена пикселями при рисовании нет: в JS уходят только `getImageData` и
//! `toDataURL`, а в окно — сам холст (см. `show.ts`).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;

use deno_core::op2;
use tiny_skia::{Mask, Rect};

mod png;
mod sheet;
pub mod text;

use sheet::{Sheet, State};

thread_local! {
  /// Холсты игры: у каждого свои пиксели.
  static SHEETS: RefCell<HashMap<u32, Sheet>> = RefCell::new(HashMap::new());
  /// Маски обрезки (`clip`): обёртка держит их номера в своём стеке состояний и отпускает, когда они больше не нужны.
  static MASKS: RefCell<HashMap<u32, Mask>> = RefCell::new(HashMap::new());
  /// Следующий номер холста или маски. Номера не переиспользуются: старый номер даст «ничего», а не чужое.
  static NEXT: Cell<u32> = const { Cell::new(1) };
}

fn next() -> u32 {
  NEXT.with(|next| {
    let id = next.get();
    next.set(id + 1);
    id
  })
}

/// Работа с холстом и маской его обрезки. Нет холста — нет рисования: как и в WebGL, чужой номер ничего не делает.
fn with<R>(id: u32, state: &State, f: impl FnOnce(&mut Sheet, Option<&Mask>) -> R) -> Option<R> {
  MASKS.with(|masks| {
    let masks = masks.borrow();
    let mask = if state.mask > 0 { masks.get(&state.mask) } else { None };
    SHEETS.with(|sheets| sheets.borrow_mut().get_mut(&id).map(|sheet| f(sheet, mask)))
  })
}

/// Числа из байтов массива обёртки (`Float32Array` пути, `Float64Array` состояния).
fn floats32(bytes: &[u8]) -> Vec<f32> {
  bytes.chunks_exact(4).map(|chunk| f32::from_le_bytes(chunk.try_into().unwrap())).collect()
}

fn state(bytes: &[u8]) -> State {
  let values: Vec<f64> = bytes.chunks_exact(8).map(|chunk| f64::from_le_bytes(chunk.try_into().unwrap())).collect();
  State::read(&values)
}

/// Заводит холст и отдаёт его номер. Пустой холст, как и у браузера: одни прозрачные пиксели.
#[op2(fast)]
pub fn op_canvas_create(width: u32, height: u32) -> u32 {
  let id = next();
  SHEETS.with(|sheets| sheets.borrow_mut().insert(id, Sheet::new(width, height)));
  id
}

/// Смена размера холста: содержимое теряется — как и у холста в браузере.
#[op2(fast)]
pub fn op_canvas_resize(id: u32, width: u32, height: u32) {
  SHEETS.with(|sheets| sheets.borrow_mut().get_mut(&id).map(|sheet| sheet.resize(width, height)));
}

/// Заливка пути цветом 0xRRGGBBAA: `fill`, `fillRect`. Путь — командами (см. `sheet::path`).
#[op2(fast)]
pub fn op_canvas_fill(id: u32, #[buffer] path: &[u8], #[buffer] values: &[u8], color: u32, evenodd: bool) {
  let Some(path) = sheet::path(&floats32(path)) else { return };
  let state = state(values);
  with(id, &state, |sheet, mask| sheet.fill(&path, color, &state, mask, evenodd));
}

/// Обводка пути: `stroke`, `strokeRect`.
#[op2(fast)]
pub fn op_canvas_stroke(id: u32, #[buffer] path: &[u8], #[buffer] values: &[u8], color: u32) {
  let Some(path) = sheet::path(&floats32(path)) else { return };
  let state = state(values);
  with(id, &state, |sheet, mask| sheet.stroke(&path, color, &state, mask));
}

/// Стирает прямоугольник: `clearRect`.
#[op2(fast)]
pub fn op_canvas_clear(id: u32, x: f64, y: f64, width: f64, height: f64, #[buffer] values: &[u8]) {
  let Some(rect) = Rect::from_xywh(x as f32, y as f32, width as f32, height as f32) else { return };
  let state = state(values);
  with(id, &state, |sheet, mask| sheet.clear(rect, &state, mask));
}

/// Обрезка по пути: новая маска — пересечение прежней (из состояния) с путём. Отдаёт номер маски для состояния.
#[op2(fast)]
pub fn op_canvas_clip(id: u32, #[buffer] path: &[u8], #[buffer] values: &[u8], evenodd: bool) -> u32 {
  let state = state(values);
  let Some(path) = sheet::path(&floats32(path)) else {
    // Пустой путь обрезает всё: маска без единого видимого пикселя.
    let mask = SHEETS.with(|sheets| sheets.borrow().get(&id).and_then(|sheet| Mask::new(sheet.width.max(1), sheet.height.max(1))));
    return mask.map_or(0, |mask| {
      let mask_id = next();
      MASKS.with(|masks| masks.borrow_mut().insert(mask_id, mask));
      mask_id
    });
  };
  let made = with(id, &state, |sheet, previous| sheet.clip(&path, &state, previous, evenodd)).flatten();
  made.map_or(0, |mask| {
    let mask_id = next();
    MASKS.with(|masks| masks.borrow_mut().insert(mask_id, mask));
    mask_id
  })
}

/// Маска больше не нужна: её состояние сняли со стека (`restore`).
#[op2(fast)]
pub fn op_canvas_unclip(mask: u32) {
  MASKS.with(|masks| masks.borrow_mut().remove(&mask));
}

/// Переносит часть одного холста на другой: `drawImage`. Источник берётся из реестра на время переноса: так два
/// холста не приходится держать взаймы сразу. Холст на самого себя рисуется через копию.
#[op2(fast)]
#[allow(clippy::too_many_arguments)]
pub fn op_canvas_draw_image(id: u32, source: u32, sx: f64, sy: f64, sw: f64, sh: f64, dx: f64, dy: f64, dw: f64, dh: f64, #[buffer] values: &[u8]) {
  let state = state(values);
  let (sx, sy, sw, sh, dx, dy, dw, dh) = (sx as f32, sy as f32, sw as f32, sh as f32, dx as f32, dy as f32, dw as f32, dh as f32);
  if source == id {
    let copy = SHEETS.with(|sheets| sheets.borrow().get(&id).map(|sheet| Sheet { width: sheet.width, height: sheet.height, pixmap: sheet.pixmap.clone() }));
    if let Some(copy) = copy {
      with(id, &state, |target, mask| target.draw(&copy, sx, sy, sw, sh, dx, dy, dw, dh, &state, mask));
    }
    return;
  }
  let Some(image) = SHEETS.with(|sheets| sheets.borrow_mut().remove(&source)) else { return };
  with(id, &state, |target, mask| target.draw(&image, sx, sy, sw, sh, dx, dy, dw, dh, &state, mask));
  SHEETS.with(|sheets| sheets.borrow_mut().insert(source, image));
}

/// Кладёт пиксели `ImageData` в холст: `putImageData`.
#[op2(fast)]
pub fn op_canvas_put_image_data(id: u32, x: i32, y: i32, width: u32, height: u32, #[buffer] data: &[u8]) {
  SHEETS.with(|sheets| {
    if let Some(sheet) = sheets.borrow_mut().get_mut(&id) {
      sheet.put_image_data(x as i64, y as i64, width as usize, height as usize, data);
    }
  });
}

/// Читает прямоугольник холста: `getImageData`. Пиксели уходят копией — игра правит их у себя.
#[op2]
#[buffer]
pub fn op_canvas_get_image_data(id: u32, x: i32, y: i32, width: u32, height: u32) -> Vec<u8> {
  let (width, height) = (width as usize, height as usize);
  SHEETS
    .with(|sheets| sheets.borrow().get(&id).map(|sheet| sheet.get_image_data(x as i64, y as i64, width, height)))
    .unwrap_or_else(|| vec![0; width * height * 4])
}

/// Текст: `fillText` и `strokeText`. align — 0 к началу, 1 по середине, 2 к концу; baseline — 0 алфавитная,
/// 1 верх, 2 висячая, 3 середина, 4 иероглифическая, 5 низ.
#[op2(fast)]
#[allow(clippy::too_many_arguments)]
pub fn op_canvas_text(id: u32, #[string] text: &str, #[string] font: &str, x: f64, y: f64, align: u32, baseline: u32, #[buffer] values: &[u8], color: u32, stroke: bool) {
  let state = state(values);
  let line = text::lay(&text::spec(font), text);
  let x = x as f32 - match align {
    1 => line.width / 2.0,
    2 => line.width,
    _ => 0.0,
  };
  let y = y as f32
    + match baseline {
      1 => line.ascent,
      2 => line.ascent * 0.8,
      3 => (line.ascent - line.descent) / 2.0,
      4 | 5 => -line.descent,
      _ => 0.0,
    };
  let Some(path) = text::outline(&line, x, y) else { return };
  with(id, &state, |sheet, mask| {
    if stroke {
      sheet.stroke(&path, color, &state, mask)
    } else {
      sheet.fill(&path, color, &state, mask, false)
    }
  });
}

/// Меры строки: `measureText` — ширина и границы шрифта над и под базовой линией, тремя `f32` подряд.
#[op2]
#[buffer]
pub fn op_canvas_measure(#[string] font: &str, #[string] text: &str) -> Vec<u8> {
  let line = text::lay(&text::spec(font), text);
  [line.width, line.ascent, line.descent].iter().flat_map(|value| value.to_le_bytes()).collect()
}

/// Картинка холста в PNG текстом: её берёт `toDataURL` — скины и значки идут в стили, портреты в `<img>`.
#[op2]
#[string]
pub fn op_canvas_png(id: u32) -> String {
  SHEETS
    .with(|sheets| sheets.borrow().get(&id).map(|sheet| png::base64(&png::png(sheet.width as usize, sheet.height as usize, &sheet.pixels()))))
    .unwrap_or_default()
}

deno_core::extension!(
  kharos_canvas,
  ops = [
    op_canvas_create,
    op_canvas_resize,
    op_canvas_fill,
    op_canvas_stroke,
    op_canvas_clear,
    op_canvas_clip,
    op_canvas_unclip,
    op_canvas_draw_image,
    op_canvas_put_image_data,
    op_canvas_get_image_data,
    op_canvas_text,
    op_canvas_measure,
    op_canvas_png,
  ],
);
