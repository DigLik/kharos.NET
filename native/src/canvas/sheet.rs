//! Холст в памяти: пиксели Skia и то, что с ними делает Canvas 2D — пути, обводки, картинки, обрезка.
//!
//! Skia — тот самый растеризатор, что рисует холст в Chrome, а `tiny-skia` был его урезанной копией: и сглаживание
//! краёв, и правила заливки, и наложение здесь те же, что у страницы. Пиксели Skia держит с цветом, умноженным на
//! альфу; игре же они отдаются как в `ImageData` — без умножения (см. `get_image_data`, `put_image_data`). Считает это
//! умножение наш код: у Skia свои округления, и через её преобразования значения бы сдвинулись.
//!
//! Состояние рисования (преобразование, прозрачность, обрезка, линия) ведёт обёртка, как у страницы, и присылает его
//! с каждым вызовом числами — см. `State::read`.

use skia_safe::{
  paint::{Cap, Join, Style},
  surfaces, AlphaType, BlendMode, Canvas, ClipOp, Color, ColorType, CubicResampler, FilterMode, Image, ImageInfo, Matrix, MipmapMode, Paint, Path, PathBuilder, PathFillType, Rect, SamplingOptions, Surface, TileMode,
};

/// Пиксели холста: RGBA, цвет умножен на альфу. Порядок байтов задаётся явно, чтобы он не зависел от платформы
/// (на macOS родной порядок — BGRA).
fn image_info(width: i32, height: i32) -> ImageInfo {
  ImageInfo::new((width, height), ColorType::RGBA8888, AlphaType::Premul, None)
}

/// Холст: пиксели и их размеры. Пустой (нулевой стороной) холст законен, как и у страницы, — пикселей у него нет.
pub struct Sheet {
  pub width: u32,
  pub height: u32,
  pub surface: Option<Surface>,
}

/// Состояние рисования одним вызовом: что обёртка ведёт у себя (см. `_state` в `js/prelude.js`).
pub struct State {
  pub transform: Matrix,
  pub alpha: f32,
  /// Номер маски обрезки; ноль — без обрезки.
  pub mask: u32,
  pub quality: SamplingOptions,
  pub blend: BlendMode,
  pub stroke: Stroke,
}

/// Линия: толщина, концы, стыки и предел острого стыка. Пунктира (`setLineDash`) холст не рисует — и не рисовал.
pub struct Stroke {
  pub width: f32,
  pub cap: Cap,
  pub join: Join,
  pub miter: f32,
}

impl State {
  /// Числа обёртки по порядку: матрица (a, b, c, d, e, f), прозрачность, маска, сглаживание картинок (0 — без него,
  /// 1 — обычное, 2 — высокое), режим наложения, толщина линии, концы (0 — срез, 1 — круг, 2 — квадрат), стыки
  /// (0 — острый, 1 — круглый, 2 — срезанный) и предел острого стыка.
  pub fn read(values: &[f64]) -> Self {
    let at = |index: usize| values.get(index).copied().unwrap_or(0.0);
    let f = |index: usize| at(index) as f32;
    let stroke = Stroke { width: f(10).max(0.0), cap: [Cap::Butt, Cap::Round, Cap::Square][(at(11) as usize).min(2)], join: [Join::Miter, Join::Round, Join::Bevel][(at(12) as usize).min(2)], miter: f(13).max(1.0) };
    Self {
      // Числа холста a, b, c, d, e, f — это увеличение X, наклон Y, наклон X, увеличение Y, сдвиг X, сдвиг Y.
      transform: Matrix::new_all(f(0), f(2), f(4), f(1), f(3), f(5), 0.0, 0.0, 1.0),
      alpha: f(6).clamp(0.0, 1.0),
      mask: at(7) as u32,
      quality: [SamplingOptions::new(FilterMode::Nearest, MipmapMode::None), SamplingOptions::new(FilterMode::Linear, MipmapMode::None), SamplingOptions::from(CubicResampler::mitchell())][(at(8) as usize).min(2)],
      blend: blend(at(9) as u32),
      stroke,
    }
  }
}

/// Режим наложения по номеру — порядок тот же, что у `COMPOSITE` в `js/prelude.js`. Имена у Skia свои: `source-over`
/// здесь `SrcOver`, `destination-atop` — `DstATop`.
fn blend(code: u32) -> BlendMode {
  use BlendMode::*;
  const MODES: [BlendMode; 26] = [SrcOver, SrcIn, SrcOut, SrcATop, DstOver, DstIn, DstOut, DstATop, Plus, Src, Xor, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity];
  MODES.get(code as usize).copied().unwrap_or(SrcOver)
}

/// Путь обёртки: числа командами — 0 начало (x, y), 1 линия (x, y), 2 квадратичная кривая (x1, y1, x, y),
/// 3 кубическая (x1, y1, x2, y2, x, y), 4 замыкание. Пустой путь — пустой путь: рисовать по нему нечего.
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
        builder.move_to((x, y));
      }
      1 => {
        let &[x, y] = take(2)? else { return None };
        builder.line_to((x, y));
      }
      2 => {
        let &[x1, y1, x, y] = take(4)? else { return None };
        builder.quad_to((x1, y1), (x, y));
      }
      3 => {
        let &[x1, y1, x2, y2, x, y] = take(6)? else { return None };
        builder.cubic_to((x1, y1), (x2, y2), (x, y));
      }
      _ => {
        builder.close();
      }
    }
  }
  Some(builder.detach())
}

/// Маска обрезки: пути, вписанные в пиксели холста в час обрезки. Skia держит обрезку в координатах холста, поэтому
/// преобразование момента обрезки вписывается в путь сразу: иначе поздний вызов со своим преобразованием увёл бы её.
pub struct Mask {
  segments: Vec<Path>,
}

impl Mask {
  /// Маска, которая ничего не обрезает: путей в ней нет.
  pub fn new(_width: u32, _height: u32) -> Option<Self> {
    Some(Self { segments: Vec::new() })
  }

  /// Маска, которая не пропускает ничего: ею оборачивается обрезка пустым путём.
  pub fn hidden(_width: u32, _height: u32) -> Option<Self> {
    Some(Self { segments: vec![Path::default()] })
  }

  /// Ещё одна обрезка: `clip` поверх прежней сужает её, а не заменяет.
  pub fn add(&mut self, path: &Path, evenodd: bool, transform: &Matrix) {
    self.segments.push(baked(path, evenodd, transform));
  }

  /// Накладывает обрезку на холст: дальше рисует тот, кто позвал (см. `Sheet::draw_with`).
  pub fn apply(&self, canvas: &Canvas) {
    for segment in &self.segments {
      canvas.clip_path(segment, ClipOp::Intersect, true);
    }
  }
}

/// Путь, вписанный в координаты холста на момент обрезки.
fn baked(path: &Path, evenodd: bool, transform: &Matrix) -> Path {
  let mut path = path.clone();
  if evenodd {
    path.set_fill_type(PathFillType::EvenOdd);
  }
  path.with_transform(transform)
}

/// Краска сплошным цветом: цвет 0xRRGGBBAA, умноженный на общую прозрачность, и режим наложения.
fn solid(value: u32, state: &State) -> Paint {
  let [r, g, b, a] = value.to_be_bytes();
  let mut paint = Paint::default();
  paint.set_color(Color::from_argb(a, r, g, b));
  paint.set_alpha_f(state.alpha);
  paint.set_blend_mode(state.blend);
  paint.set_anti_alias(true);
  paint
}

impl Sheet {
  pub fn new(width: u32, height: u32) -> Self {
    let surface = (width > 0 && height > 0).then(|| surfaces::raster(&image_info(width as i32, height as i32), None, None)).flatten();
    Self { width, height, surface }
  }

  /// Холст из готовой картинки: так читается PNG (`new Image()` с `src`).
  pub fn from_image(image: &Image, width: u32, height: u32) -> Self {
    let mut sheet = Self::new(width, height);
    if let Some(surface) = sheet.surface.as_mut() {
      surface.canvas().draw_image(image, (0.0, 0.0), None);
    }
    sheet
  }

  /// Смена размера: содержимое теряется — как и у холста в браузере.
  pub fn resize(&mut self, width: u32, height: u32) {
    *self = Self::new(width, height);
  }

  /// Рисует с обрезкой и своим преобразованием: обрезка накладывается на время вызова, как у страницы.
  fn draw_with(&mut self, mask: Option<&Mask>, transform: &Matrix, draw: impl FnOnce(&Canvas)) {
    let Some(surface) = self.surface.as_mut() else { return };
    let canvas = surface.canvas();
    canvas.save();
    if let Some(mask) = mask {
      mask.apply(canvas);
    }
    canvas.concat(transform);
    draw(canvas);
    canvas.restore();
  }

  /// Заливка пути цветом: `fill`, `fillRect`, текст. evenodd — правило «чётное-нечётное» вместо «ненулевого».
  pub fn fill(&mut self, path: &Path, value: u32, state: &State, mask: Option<&Mask>, evenodd: bool) {
    let mut path = path.clone();
    if evenodd {
      path.set_fill_type(PathFillType::EvenOdd);
    }
    let paint = solid(value, state);
    self.draw_with(mask, &state.transform, |canvas| {
      canvas.draw_path(&path, &paint);
    });
  }

  /// Обводка пути: `stroke`, `strokeRect`.
  pub fn stroke(&mut self, path: &Path, value: u32, state: &State, mask: Option<&Mask>) {
    let mut paint = solid(value, state);
    paint.set_style(Style::Stroke);
    paint.set_stroke_width(state.stroke.width);
    paint.set_stroke_cap(state.stroke.cap);
    paint.set_stroke_join(state.stroke.join);
    paint.set_stroke_miter(state.stroke.miter);
    self.draw_with(mask, &state.transform, |canvas| {
      canvas.draw_path(path, &paint);
    });
  }

  /// Стирает прямоугольник до прозрачного: `clearRect`. Прозрачность и режим наложения на него не действуют.
  pub fn clear(&mut self, rect: Rect, state: &State, mask: Option<&Mask>) {
    let mut paint = Paint::default();
    paint.set_blend_mode(BlendMode::Clear);
    self.draw_with(mask, &state.transform, |canvas| {
      canvas.draw_rect(rect, &paint);
    });
  }

  /// Снимок пикселей: `drawImage` берёт источник себе, а прежний холст продолжает рисовать. Снимок дешевле копии
  /// пикселей — Skia копирует их только тогда, когда холст после снимка изменится.
  pub fn snapshot(&mut self) -> Option<Image> {
    self.surface.as_mut().map(|surface| surface.image_snapshot())
  }

  /// Перенос части другого холста на этот: `drawImage`. Картинка ложится узором на прямоугольник назначения — так
  /// работают и растяжение, и обрезка, и преобразование, а без сглаживания пиксели остаются пикселями.
  #[allow(clippy::too_many_arguments)]
  pub fn draw_image(&mut self, image: &Image, sx: f32, sy: f32, sw: f32, sh: f32, dx: f32, dy: f32, dw: f32, dh: f32, state: &State, mask: Option<&Mask>) {
    if sw == 0.0 || sh == 0.0 {
      return;
    }
    let mut place = Matrix::scale_translate((dw / sw, dh / sh), (dx, dy));
    place.pre_translate((-sx, -sy));
    let Some(shader) = image.to_shader((TileMode::Clamp, TileMode::Clamp), state.quality, &place) else { return };
    let mut paint = Paint::default();
    paint.set_shader(shader);
    paint.set_alpha_f(state.alpha);
    paint.set_blend_mode(state.blend);
    // Край картинки на целых пикселях не размывается, а на дробных сглаживается — как у страницы.
    paint.set_anti_alias(true);
    let rect = Rect::from_xywh(dx, dy, dw, dh);
    self.draw_with(mask, &state.transform, |canvas| {
      canvas.draw_rect(rect, &paint);
    });
  }

  /// Обрезка по пути: новая маска — прежняя обрезка и этот путь.
  pub fn clip(&self, path: &Path, state: &State, previous: Option<&Mask>, evenodd: bool) -> Option<Mask> {
    let mut mask = match previous {
      Some(previous) => Mask { segments: previous.segments.clone() },
      None => Mask::new(self.width, self.height)?,
    };
    mask.add(path, evenodd, &state.transform);
    Some(mask)
  }
}

impl Sheet {
  /// Пиксели для `ImageData`: без умножения на альфу и без обрезки с преобразованием (`putImageData` не смешивает).
  pub fn get_image_data(&mut self, x: i64, y: i64, width: usize, height: usize) -> Vec<u8> {
    let (own_width, own_height) = (self.width as i64, self.height as i64);
    let mut out = vec![0u8; width * height * 4];
    let Some(surface) = self.surface.as_mut() else { return out };
    let Some(pixmap) = surface.peek_pixels() else { return out };
    let Some(pixels) = pixmap.bytes() else { return out };
    let row_bytes = pixmap.row_bytes();
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
        let from = ty as usize * row_bytes + tx as usize * 4;
        let [r, g, b, a] = [pixels[from], pixels[from + 1], pixels[from + 2], pixels[from + 3]];
        let divide = |c: u8| if a == 0 { 0 } else { ((c as u32 * 255 + a as u32 / 2) / a as u32).min(255) as u8 };
        let to = ((row * width as i64 + column) * 4) as usize;
        out[to..to + 4].copy_from_slice(&[divide(r), divide(g), divide(b), a]);
      }
    }
    out
  }

  /// Пиксели как в `ImageData` — для PNG и текстуры без умножения на альфу.
  pub fn pixels(&mut self) -> Vec<u8> {
    let (width, height) = (self.width as usize, self.height as usize);
    self.get_image_data(0, 0, width, height)
  }

  /// Пиксели с умноженным на альфу цветом — так их ждёт текстура (`texImage2D` с холстом в роли источника).
  /// Копии нет: это память самой поверхности.
  pub fn premultiplied(&mut self) -> Option<&[u8]> {
    let surface = self.surface.as_mut()?;
    surface.peek_pixels()?.bytes()
  }

  /// Кладёт пиксели `ImageData` как есть: без смешивания, преобразования и обрезки — так велит `putImageData`.
  /// Умножение цвета на альфу — наша арифметика: у Skia округления свои, и через её преобразования значения бы
  /// сдвинулись (так же считал и `tiny-skia`).
  pub fn put_image_data(&mut self, x: i64, y: i64, width: usize, height: usize, data: &[u8]) {
    let (own_width, own_height) = (self.width as i64, self.height as i64);
    let Some(surface) = self.surface.as_mut() else { return };
    let Some(mut pixmap) = surface.peek_pixels() else { return };
    let row_bytes = pixmap.row_bytes();
    let Some(pixels) = pixmap.bytes_mut() else { return };
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
        let to = ty as usize * row_bytes + tx as usize * 4;
        let multiply = |c: u8| ((c as u32 * a as u32 + 127) / 255) as u8;
        pixels[to..to + 4].copy_from_slice(&[multiply(r), multiply(g), multiply(b), a]);
      }
    }
  }
}
