//! Привязки двумерного холста — Canvas 2D API. Им игра рисует картинки кодом (скины, значки, портреты, мини-карту), а
//! интерфейс рисует себя целиком (пункт 10.4 плана): в браузере это родной холст, здесь — Skia и свой текст.
//!
//! Устройство то же, что у привязок GL: игра видит холст числом, а Rust держит за ним пиксели. Обёртка
//! (`js/prelude.js`) ведёт браузерную сторону — цвета, пути, состояние и его стек (`save`/`restore`), — и присылает с
//! каждым вызовом то, что нужно для рисования. Обмена пикселями при рисовании нет: в JS уходят только `getImageData` и
//! `toDataURL`, а в окно — сам холст (см. `show.ts`).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::time::{Duration, Instant};

use deno_core::op2;
use skia_safe::Rect;

mod png;
mod sheet;
pub mod text;

use sheet::{Mask, Sheet, State};

thread_local! {
  /// Холсты игры: у каждого свои пиксели.
  static SHEETS: RefCell<HashMap<u32, Sheet>> = RefCell::new(HashMap::new());
  /// Маски обрезки (`clip`): обёртка держит их номера в своём стеке состояний и отпускает, когда они больше не нужны.
  static MASKS: RefCell<HashMap<u32, Mask>> = RefCell::new(HashMap::new());
  /// Следующий номер холста или маски. Номера не переиспользуются: старый номер даст «ничего», а не чужое.
  static NEXT: Cell<u32> = const { Cell::new(1) };
  /// Время рисования по холстам с прошлого замера. None — не замеряется (см. `profile.rs`).
  static COSTS: RefCell<Option<Costs>> = const { RefCell::new(None) };
}

/// Включает замер времени рисования по холстам.
pub fn measure_costs() {
  COSTS.with(|costs| *costs.borrow_mut() = Some(HashMap::new()));
}

/// Время рисования по холстам с прошлого вызова: номер, ширина, высота, время.
pub fn take_costs() -> Vec<(u32, u32, u32, Duration)> {
  COSTS.with(|costs| {
    costs.borrow_mut().as_mut().map_or_else(Vec::new, |costs| costs.drain().map(|(id, (width, height, time))| (id, width, height, time)).collect())
  })
}

/// Время рисования по холстам: номер — ширина, высота и сумма за срок замера.
type Costs = HashMap<u32, (u32, u32, Duration)>;

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
    SHEETS.with(|sheets| {
      let mut sheets = sheets.borrow_mut();
      let sheet = sheets.get_mut(&id)?;
      let measured = COSTS.with(|costs| costs.borrow().is_some());
      if !measured {
        return Some(f(sheet, mask));
      }
      let begun = Instant::now();
      let (width, height) = (sheet.width, sheet.height);
      let result = f(sheet, mask);
      COSTS.with(|costs| {
        if let Some(costs) = costs.borrow_mut().as_mut() {
          let cost = costs.entry(id).or_insert((width, height, Duration::ZERO));
          *cost = (width, height, cost.2 + begun.elapsed());
        }
      });
      Some(result)
    })
  })
}

/// Пиксели холста для текстуры (`texImage2D` с холстом в роли источника, см. `gl::op_gl_tex_image_canvas`):
/// premultiplied — с цветом, умноженным на альфу (так их и держит холст), иначе — как в `ImageData`.
pub fn with_pixels<R>(id: u32, premultiplied: bool, f: impl FnOnce(u32, u32, &[u8]) -> R) -> Option<R> {
  SHEETS.with(|sheets| {
    let mut sheets = sheets.borrow_mut();
    let sheet = sheets.get_mut(&id)?;
    let (width, height) = (sheet.width, sheet.height);
    if premultiplied {
      // С умноженным на альфу цветом пиксели отдаются как есть: это память самой поверхности, копии нет.
      return sheet.premultiplied().map(|pixels| f(width, height, pixels));
    }
    Some(f(width, height, &sheet.pixels()))
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

/// Стирает прямоугольник: `clearRect`. Пустой или вывернутый прямоугольник стирать нечего — как и у страницы.
#[op2(fast)]
pub fn op_canvas_clear(id: u32, x: f64, y: f64, width: f64, height: f64, #[buffer] values: &[u8]) {
  let (x, y, width, height) = (x as f32, y as f32, width as f32, height as f32);
  // Сравнением, а не `<= 0.0`: так и NaN в размерах стирает пусто.
  let positive = width > 0.0 && height > 0.0;
  if !positive || !x.is_finite() || !y.is_finite() {
    return;
  }
  let state = state(values);
  with(id, &state, |sheet, mask| sheet.clear(Rect::from_xywh(x, y, width, height), &state, mask));
}

/// Обрезка по пути: новая маска — пересечение прежней (из состояния) с путём. Отдаёт номер маски для состояния.
#[op2(fast)]
pub fn op_canvas_clip(id: u32, #[buffer] path: &[u8], #[buffer] values: &[u8], evenodd: bool) -> u32 {
  let state = state(values);
  let Some(path) = sheet::path(&floats32(path)) else {
    // Пустой путь обрезает всё: маска без единого видимого пикселя.
    let mask = SHEETS.with(|sheets| sheets.borrow().get(&id).and_then(|sheet| Mask::hidden(sheet.width.max(1), sheet.height.max(1))));
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

/// Переносит часть одного холста на другой: `drawImage`. Источник берётся снимком: снимок сам держит пиксели, поэтому
/// холст не приходится держать взаймы всё время рисования. Холст на самого себя тоже идёт снимком.
#[op2(fast)]
#[allow(clippy::too_many_arguments)]
pub fn op_canvas_draw_image(id: u32, source: u32, sx: f64, sy: f64, sw: f64, sh: f64, dx: f64, dy: f64, dw: f64, dh: f64, #[buffer] values: &[u8]) {
  let state = state(values);
  let (sx, sy, sw, sh, dx, dy, dw, dh) = (sx as f32, sy as f32, sw as f32, sh as f32, dx as f32, dy as f32, dw as f32, dh as f32);
  if source == id {
    let snapshot = SHEETS.with(|sheets| sheets.borrow_mut().get_mut(&id).and_then(|sheet| sheet.snapshot()));
    if let Some(image) = snapshot {
      with(id, &state, |target, mask| target.draw_image(&image, sx, sy, sw, sh, dx, dy, dw, dh, &state, mask));
    }
    return;
  }
  let snapshot = SHEETS.with(|sheets| {
    let mut sheets = sheets.borrow_mut();
    let mut taken = sheets.remove(&source)?;
    let image = taken.snapshot();
    sheets.insert(source, taken);
    image
  });
  let Some(image) = snapshot else { return };
  with(id, &state, |target, mask| target.draw_image(&image, sx, sy, sw, sh, dx, dy, dw, dh, &state, mask));
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
    .with(|sheets| sheets.borrow_mut().get_mut(&id).map(|sheet| sheet.get_image_data(x as i64, y as i64, width, height)))
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
  let path = text::outline(&line, x, y);
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
    .with(|sheets| sheets.borrow_mut().get_mut(&id).map(|sheet| png::base64(&png::png(sheet.width as usize, sheet.height as usize, &sheet.pixels()))))
    .unwrap_or_default()
}

/// Где лежат файлы страницы (`public/` игры: значок и шрифты страницы): `KHAROS_PUBLIC`, иначе рядом с окном.
fn public_dir() -> std::path::PathBuf {
  std::env::var_os("KHAROS_PUBLIC").map(std::path::PathBuf::from).unwrap_or_else(|| std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../public"))
}

/// Картинка страницы (`new Image()` с `src`): PNG из `public/` — в новый холст. Отдаёт номер холста, ширину и высоту;
/// файла нет или это не PNG — пусто, и обёртка зовёт `onerror`. Адрес — путь от корня страницы: `/favicon.png`.
#[op2]
#[serde]
pub fn op_canvas_load_image(#[string] src: &str) -> Vec<u32> {
  let path = src.split(['?', '#']).next().unwrap_or_default().trim_start_matches("./").trim_start_matches('/');
  // Только внутри public: подняться выше по `..` адрес не может.
  if path.is_empty() || path.split('/').any(|part| part == "..") {
    return Vec::new();
  }
  let file = public_dir().join(path);
  let bytes = match std::fs::read(&file) {
    Ok(bytes) => bytes,
    Err(error) => {
      eprintln!("картинка {} не прочитана: {error}", file.display());
      return Vec::new();
    }
  };
  let Some(image) = skia_safe::Image::from_encoded(skia_safe::Data::new_copy(&bytes)) else {
    eprintln!("картинка {} не разобрана: это не PNG или файл испорчен", file.display());
    return Vec::new();
  };
  let (width, height) = (image.width() as u32, image.height() as u32);
  let id = next();
  SHEETS.with(|sheets| sheets.borrow_mut().insert(id, Sheet::from_image(&image, width, height)));
  vec![id, width, height]
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
    op_canvas_load_image,
  ],
);
