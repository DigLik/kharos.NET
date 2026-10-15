//! Привязки двумерного холста: то, чем интерфейс рисует картинки, — портреты, значки, скины и мини-карту.
//!
//! В браузере это `CanvasRenderingContext2D`, и игра пользуется им как вычислителем: рисует картинку кодом
//! (`src/ui/skin.ts`, `src/game/portraits.ts`, `src/game/goodIcons.ts`), ведёт по ней миникарту
//! (`src/game/minimap.ts`) и отдаёт её стилям и `<img>` строкой (`toDataURL`). Значит нужен не показ холста —
//! показ придёт вместе с документом, — а сами пиксели.
//!
//! Устройство то же, что у привязок GL: игра видит холст числом, а Rust держит за ним пиксели. Обёртка
//! (`js/prelude.js`) ведёт браузерную сторону — цвета, пути, `ImageData`, — а тяжёлое (растеризация) здесь.
//! Обмена при рисовании нет: холст живёт в Rust целиком, и в JS уходят только `getImageData` и `toDataURL`.

use std::cell::{Cell, RefCell};
use std::collections::HashMap;

use deno_core::op2;

mod paint;

use paint::{base64, Sheet};

thread_local! {
  /// Холсты игры: у каждого свои пиксели. Живут до конца игры, как и контекст драйвера.
  static SHEETS: RefCell<HashMap<u32, Sheet>> = RefCell::new(HashMap::new());
  /// Следующий номер холста. Номера не переиспользуются: игра, оставившая старый, получит «ничего», а не чужое.
  static NEXT: Cell<u32> = const { Cell::new(1) };
}

/// Берёт холст по номеру. Нет холста — нет рисования: как и в WebGL, чужой номер ничего не делает.
fn with<R>(id: u32, f: impl FnOnce(&mut Sheet) -> R) -> Option<R> {
  SHEETS.with(|sheets| sheets.borrow_mut().get_mut(&id).map(f))
}

/// Число в цвет: обёртка присылает 0xRRGGBBAA — цвет и альфа одной посылкой.
fn color(value: u32) -> [u8; 4] {
  [(value >> 24) as u8, (value >> 16) as u8, (value >> 8) as u8, value as u8]
}

/// Заводит холст и отдаёт его номер. Пустой холст, как и у браузера: одни прозрачные пиксели.
#[op2(fast)]
pub fn op_canvas_create(width: u32, height: u32) -> u32 {
  let id = NEXT.with(|next| {
    let id = next.get();
    next.set(id + 1);
    id
  });
  SHEETS.with(|sheets| sheets.borrow_mut().insert(id, Sheet::new(width as usize, height as usize)));
  id
}

/// Смена размера холста: содержимое теряется — как и у холста в браузере.
#[op2(fast)]
pub fn op_canvas_resize(id: u32, width: u32, height: u32) {
  with(id, |sheet| sheet.resize(width as usize, height as usize));
}

/// Заливка прямоугольника: `fillRect`.
#[op2(fast)]
pub fn op_canvas_fill_rect(id: u32, x: f64, y: f64, width: f64, height: f64, value: u32, alpha: f64) {
  with(id, |sheet| sheet.fill_rect(x, y, width, height, color(value), alpha));
}

/// Обводка прямоугольника: `strokeRect`.
#[op2(fast)]
pub fn op_canvas_stroke_rect(id: u32, x: f64, y: f64, width: f64, height: f64, line: f64, value: u32, alpha: f64) {
  with(id, |sheet| sheet.stroke_rect(x, y, width, height, line, color(value), alpha));
}

/// Путь: либо обводка, либо заливка. Путь приходит числами (`Float32Array`) тройками: вид точки (0 — начало
/// подпути, 1 — продолжение), x, y. Дуги обёртка дробит на отрезки сама, поэтому здесь только линии и заливка контура.
#[op2(fast)]
pub fn op_canvas_path(id: u32, #[buffer] path: &[u8], value: u32, line: f64, alpha: f64, fill: bool) {
  let path: Vec<f64> = path.chunks_exact(4).map(|chunk| f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]) as f64).collect();
  let color = color(value);
  with(id, |sheet| {
    if fill {
      sheet.fill_path(&path, color, alpha)
    } else {
      sheet.stroke_path(&path, line, color, alpha)
    }
  });
}

/// Кладёт пиксели `ImageData` в холст: `putImageData`.
#[op2(fast)]
pub fn op_canvas_put_image_data(id: u32, x: i32, y: i32, width: u32, height: u32, #[buffer] data: &[u8]) {
  with(id, |sheet| sheet.put_image_data(x as i64, y as i64, width as usize, height as usize, data));
}

/// Читает прямоугольник холста: `getImageData`. Пиксели уходят копией — игра правит их у себя.
#[op2]
#[buffer]
pub fn op_canvas_get_image_data(id: u32, x: i32, y: i32, width: u32, height: u32) -> Vec<u8> {
  let (width, height) = (width as usize, height as usize);
  with(id, |sheet| sheet.get_image_data(x as i64, y as i64, width, height)).unwrap_or_else(|| vec![0; width * height * 4])
}

/// Переносит картинку с одного холста на другой: `drawImage`. Источник берётся из реестра на время переноса:
/// так два холста не приходится держать взаймы сразу, а пиксели не копируются дважды.
#[op2(fast)]
#[allow(clippy::too_many_arguments)]
pub fn op_canvas_draw_image(
  id: u32,
  source: u32,
  sx: f64,
  sy: f64,
  sw: f64,
  sh: f64,
  dx: f64,
  dy: f64,
  dw: f64,
  dh: f64,
  smooth: bool,
  alpha: f64,
) {
  let Some(sheet) = SHEETS.with(|sheets| sheets.borrow_mut().remove(&source)) else {
    return;
  };
  with(id, |target| target.draw_image(&sheet, sx, sy, sw, sh, dx, dy, dw, dh, smooth, alpha));
  SHEETS.with(|sheets| sheets.borrow_mut().insert(source, sheet));
}

/// Картинка холста в PNG текстом: её берёт `toDataURL` — скины и значки идут в стили, портреты в `<img>`.
#[op2]
#[string]
pub fn op_canvas_png(id: u32) -> String {
  with(id, |sheet| base64(&sheet.to_png())).unwrap_or_default()
}

deno_core::extension!(
  kharos_canvas,
  ops = [
    op_canvas_create,
    op_canvas_resize,
    op_canvas_fill_rect,
    op_canvas_stroke_rect,
    op_canvas_path,
    op_canvas_put_image_data,
    op_canvas_get_image_data,
    op_canvas_draw_image,
    op_canvas_png,
  ],
);
