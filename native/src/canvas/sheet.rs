//! Холст в памяти: пиксели `tiny-skia` и то, что с ними делает Canvas 2D — пути, обводки, картинки, обрезка.
//!
//! `tiny-skia` — урезанный Skia, тот же растеризатор, что рисует холст в Chrome, поэтому и сглаживание краёв, и
//! правила заливки, и наложение здесь те же, что у страницы. Пиксели он держит с цветом, умноженным на альфу; игре же
//! они отдаются как в `ImageData` — без умножения (см. `get_image_data`, `put_image_data`).
//!
//! Состояние рисования (преобразование, прозрачность, обрезка, линия) ведёт обёртка, как у страницы, и присылает его
//! с каждым вызовом числами — см. `State::read`.

use tiny_skia::{
  BlendMode, Color, FillRule, FilterQuality, LineCap, LineJoin, Mask, Paint, Path, PathBuilder, Pattern, Pixmap, Rect,
  SpreadMode, Stroke, Transform,
};

/// Холст: пиксели и их размеры. Пустой (нулевой стороной) холст законен, как и у страницы, — пикселей у него нет.
pub struct Sheet {
  pub width: u32,
  pub height: u32,
  pub pixmap: Option<Pixmap>,
}

/// Состояние рисования одним вызовом: что обёртка ведёт у себя (см. `_state` в `js/prelude.js`).
pub struct State {
  pub transform: Transform,
  pub alpha: f32,
  /// Номер маски обрезки; ноль — без обрезки.
  pub mask: u32,
  pub quality: FilterQuality,
  pub blend: BlendMode,
  pub stroke: Stroke,
}

impl State {
  /// Числа обёртки по порядку: матрица (a, b, c, d, e, f), прозрачность, маска, сглаживание картинок (0 — без него,
  /// 1 — обычное, 2 — высокое), режим наложения, толщина линии, концы (0 — срез, 1 — круг, 2 — квадрат), стыки
  /// (0 — острый, 1 — круглый, 2 — срезанный) и предел острого стыка.
  pub fn read(values: &[f64]) -> Self {
    let at = |index: usize| values.get(index).copied().unwrap_or(0.0);
    let f = |index: usize| at(index) as f32;
    let stroke = Stroke {
      width: f(10).max(0.0),
      line_cap: [LineCap::Butt, LineCap::Round, LineCap::Square][(at(11) as usize).min(2)],
      line_join: [LineJoin::Miter, LineJoin::Round, LineJoin::Bevel][(at(12) as usize).min(2)],
      miter_limit: f(13).max(1.0),
      dash: None,
    };
    Self {
      transform: Transform::from_row(f(0), f(1), f(2), f(3), f(4), f(5)),
      alpha: f(6).clamp(0.0, 1.0),
      mask: at(7) as u32,
      quality: [FilterQuality::Nearest, FilterQuality::Bilinear, FilterQuality::Bicubic][(at(8) as usize).min(2)],
      blend: blend(at(9) as u32),
      stroke,
    }
  }
}

/// Режим наложения по номеру — порядок тот же, что у `COMPOSITE` в `js/prelude.js`.
fn blend(code: u32) -> BlendMode {
  use BlendMode::*;
  const MODES: [BlendMode; 26] = [
    SourceOver, SourceIn, SourceOut, SourceAtop, DestinationOver, DestinationIn, DestinationOut, DestinationAtop,
    Plus, Source, Xor, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight,
    Difference, Exclusion, Hue, Saturation, Color, Luminosity,
  ];
  MODES.get(code as usize).copied().unwrap_or(SourceOver)
}

/// Путь обёртки: числа командами — 0 начало (x, y), 1 линия (x, y), 2 квадратичная кривая (x1, y1, x, y),
/// 3 кубическая (x1, y1, x2, y2, x, y), 4 замыкание. Пустой или вырожденный путь — `None`: рисовать нечего.
pub fn path(numbers: &[f32]) -> Option<Path> {
  let mut builder = PathBuilder::new();
  let mut at = 0;
  let mut take = |count: usize| {
    let part = numbers.get(at..at + count);
    at += count;
    part
  };
  while let Some(&[command]) = take(1) {
    match command as u32 {
      0 => {
        let &[x, y] = take(2)? else { return None };
        builder.move_to(x, y);
      }
      1 => {
        let &[x, y] = take(2)? else { return None };
        builder.line_to(x, y);
      }
      2 => {
        let &[x1, y1, x, y] = take(4)? else { return None };
        builder.quad_to(x1, y1, x, y);
      }
      3 => {
        let &[x1, y1, x2, y2, x, y] = take(6)? else { return None };
        builder.cubic_to(x1, y1, x2, y2, x, y);
      }
      _ => builder.close(),
    }
  }
  builder.finish()
}

/// Краска сплошным цветом: цвет 0xRRGGBBAA, умноженный на общую прозрачность.
fn solid(value: u32, state: &State) -> Paint<'static> {
  let mut paint = Paint::default();
  let [r, g, b, a] = value.to_be_bytes();
  paint.set_color(Color::from_rgba8(r, g, b, a));
  paint.shader.apply_opacity(state.alpha);
  paint.blend_mode = state.blend;
  paint.anti_alias = true;
  paint
}

impl Sheet {
  pub fn new(width: u32, height: u32) -> Self {
    Self { width, height, pixmap: Pixmap::new(width, height) }
  }

  /// Смена размера: содержимое теряется — как и у холста в браузере.
  pub fn resize(&mut self, width: u32, height: u32) {
    *self = Self::new(width, height);
  }

  /// Заливка пути цветом: `fill`, `fillRect`, текст. evenodd — правило «чётное-нечётное» вместо «ненулевого».
  pub fn fill(&mut self, path: &Path, value: u32, state: &State, mask: Option<&Mask>, evenodd: bool) {
    let Some(pixmap) = self.pixmap.as_mut() else { return };
    let rule = if evenodd { FillRule::EvenOdd } else { FillRule::Winding };
    pixmap.fill_path(path, &solid(value, state), rule, state.transform, mask);
  }

  /// Обводка пути: `stroke`, `strokeRect`.
  pub fn stroke(&mut self, path: &Path, value: u32, state: &State, mask: Option<&Mask>) {
    let Some(pixmap) = self.pixmap.as_mut() else { return };
    pixmap.stroke_path(path, &solid(value, state), &state.stroke, state.transform, mask);
  }

  /// Стирает прямоугольник до прозрачного: `clearRect`. Прозрачность и режим наложения на него не действуют.
  pub fn clear(&mut self, rect: Rect, state: &State, mask: Option<&Mask>) {
    let Some(pixmap) = self.pixmap.as_mut() else { return };
    let mut paint = Paint::default();
    paint.blend_mode = BlendMode::Clear;
    pixmap.fill_rect(rect, &paint, state.transform, mask);
  }

  /// Перенос части другого холста: `drawImage`. Картинка ложится узором на прямоугольник назначения — так работают
  /// и растяжение, и обрезка, и преобразование, а без сглаживания пиксели остаются пикселями.
  #[allow(clippy::too_many_arguments)]
  pub fn draw(&mut self, source: &Sheet, sx: f32, sy: f32, sw: f32, sh: f32, dx: f32, dy: f32, dw: f32, dh: f32, state: &State, mask: Option<&Mask>) {
    let (Some(pixmap), Some(image)) = (self.pixmap.as_mut(), source.pixmap.as_ref()) else { return };
    let Some(rect) = Rect::from_xywh(dx, dy, dw, dh) else { return };
    if sw == 0.0 || sh == 0.0 {
      return;
    }
    let place = Transform::from_translate(dx, dy).pre_scale(dw / sw, dh / sh).pre_translate(-sx, -sy);
    let mut paint = Paint::default();
    paint.shader = Pattern::new(image.as_ref(), SpreadMode::Pad, state.quality, state.alpha, place);
    paint.blend_mode = state.blend;
    // Край картинки на целых пикселях не размывается, а на дробных сглаживается — как у страницы.
    paint.anti_alias = true;
    pixmap.fill_rect(rect, &paint, state.transform, mask);
  }

  /// Кладёт пиксели `ImageData` как есть: без смешивания, преобразования и обрезки — так велит `putImageData`.
  pub fn put_image_data(&mut self, x: i64, y: i64, width: usize, height: usize, data: &[u8]) {
    let Some(pixmap) = self.pixmap.as_mut() else { return };
    let (own_width, own_height) = (self.width as i64, self.height as i64);
    let pixels = pixmap.data_mut();
    for row in 0..height as i64 {
      let ty = y + row;
      if ty < 0 || ty >= own_height {
        continue;
      }
      for column in 0..width as i64 {
        let tx = x + column;
        if tx < 0 || tx >= own_width {
          continue;
        }
        let from = ((row * width as i64 + column) * 4) as usize;
        let Some(&[r, g, b, a]) = data.get(from..from + 4) else { continue };
        let to = ((ty * own_width + tx) * 4) as usize;
        let multiply = |c: u8| ((c as u32 * a as u32 + 127) / 255) as u8;
        pixels[to..to + 4].copy_from_slice(&[multiply(r), multiply(g), multiply(b), a]);
      }
    }
  }

  /// Читает прямоугольник пикселей без умножения на альфу; за краем холста — прозрачные.
  pub fn get_image_data(&self, x: i64, y: i64, width: usize, height: usize) -> Vec<u8> {
    let mut out = vec![0u8; width * height * 4];
    let Some(pixmap) = self.pixmap.as_ref() else { return out };
    let (own_width, own_height) = (self.width as i64, self.height as i64);
    let pixels = pixmap.data();
    for row in 0..height as i64 {
      let sy = y + row;
      if sy < 0 || sy >= own_height {
        continue;
      }
      for column in 0..width as i64 {
        let sx = x + column;
        if sx < 0 || sx >= own_width {
          continue;
        }
        let from = ((sy * own_width + sx) * 4) as usize;
        let [r, g, b, a] = [pixels[from], pixels[from + 1], pixels[from + 2], pixels[from + 3]];
        let divide = |c: u8| if a == 0 { 0 } else { ((c as u32 * 255 + a as u32 / 2) / a as u32).min(255) as u8 };
        let to = ((row * width as i64 + column) * 4) as usize;
        out[to..to + 4].copy_from_slice(&[divide(r), divide(g), divide(b), a]);
      }
    }
    out
  }

  /// Маска обрезки по пути: внутри прежней обрезки, если она была, — так `clip()` сужает её, а не заменяет.
  pub fn clip(&self, path: &Path, state: &State, previous: Option<&Mask>, evenodd: bool) -> Option<Mask> {
    let rule = if evenodd { FillRule::EvenOdd } else { FillRule::Winding };
    match previous {
      Some(mask) => {
        let mut mask = mask.clone();
        mask.intersect_path(path, rule, true, state.transform);
        Some(mask)
      }
      None => {
        let mut mask = Mask::new(self.width.max(1), self.height.max(1))?;
        mask.fill_path(path, rule, true, state.transform);
        Some(mask)
      }
    }
  }

  /// Пиксели как в `ImageData` — для PNG.
  pub fn pixels(&self) -> Vec<u8> {
    self.get_image_data(0, 0, self.width as usize, self.height as usize)
  }
}
