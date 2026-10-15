//! Растеризация двумерного холста: пиксели, прямоугольники, линии и перенос картинок.
//!
//! Стиль тот же, что у графики игры: цветные прямоугольники целыми пикселями, без сглаживания. Так рисованы и скины
//! интерфейса (`src/ui/skin.ts` — та же заливка `fillRect`), и значки, и мини-карта: у игры своя палитра, и мягкие края
//! только размывали бы её. Пиксели лежат как в `ImageData` — R, G, B, A на пиксель, и цвет не умножен на альфу: так
//! игре отдаётся холст, и так же она заливает его обратно (`putImageData`).
//!
//! Цвет приходит из обёртки числом: 0xRRGGBBAA. Строки цветов разбирает обёртка (там же, где игра их пишет), а холст
//! имеет дело уже с числами — как WebGL имеет дело с константами.

/// Целиком прозрачный пиксель: пустая клетка холста.
const CLEAR: [u8; 4] = [0, 0, 0, 0];

/// Холст: пиксели и их размеры. Размеры меняются на ходу — игра подгоняет холст под окно и под настройки.
pub struct Sheet {
  pub width: usize,
  pub height: usize,
  /// Пиксели полосами слева направо и сверху вниз, по четыре байта на пиксель.
  pub pixels: Vec<u8>,
}

impl Sheet {
  pub fn new(width: usize, height: usize) -> Self {
    Self { width, height, pixels: vec![0; width * height * 4] }
  }

  /// Смена размера: как и у холста в браузере, содержимое теряется — прежние пиксели остаются за краем.
  pub fn resize(&mut self, width: usize, height: usize) {
    self.width = width;
    self.height = height;
    self.pixels = vec![0; width * height * 4];
  }

  /// Пиксель по целым координатам. За краем — ничего: рисование обрезается по холсту, как в браузере.
  fn at(&mut self, x: i64, y: i64) -> Option<&mut [u8]> {
    if x < 0 || y < 0 || x >= self.width as i64 || y >= self.height as i64 {
      return None;
    }
    let start = (y as usize * self.width + x as usize) * 4;
    Some(&mut self.pixels[start..start + 4])
  }

  /// Накладывает цвет на пиксель: источник поверх назначения (`source-over`), как в браузере. Альфа цвета
  /// домножается на `alpha` — так же умножает `globalAlpha` в игре (сигналы на мини-карте гаснут по нему).
  fn blend(&mut self, x: i64, y: i64, color: [u8; 4], alpha: f64) {
    let source = color[3] as f64 / 255.0 * alpha;
    if source <= 0.0 {
      return;
    }
    let Some(pixel) = self.at(x, y) else { return };
    let dest = pixel[3] as f64 / 255.0;
    let out = source + dest * (1.0 - source);
    if out <= 0.0 {
      pixel.copy_from_slice(&CLEAR);
      return;
    }
    for channel in 0..3 {
      let value = (color[channel] as f64 / 255.0 * source + pixel[channel] as f64 / 255.0 * dest * (1.0 - source)) / out;
      pixel[channel] = (value * 255.0 + 0.5) as u8;
    }
    pixel[3] = (out * 255.0 + 0.5) as u8;
  }

  /// Заливает прямоугольник: границы округляются до целых пикселей — дробные доли приходят от масштаба карты.
  pub fn fill_rect(&mut self, x: f64, y: f64, width: f64, height: f64, color: [u8; 4], alpha: f64) {
    let (left, top) = (x.round() as i64, y.round() as i64);
    let (right, bottom) = ((x + width).round() as i64, (y + height).round() as i64);
    for py in top..bottom {
      for px in left..right {
        self.blend(px, py, color, alpha);
      }
    }
  }

  /// Обводка прямоугольника: та же кисть, что у линий, и ею же обведены рёбра.
  pub fn stroke_rect(&mut self, x: f64, y: f64, width: f64, height: f64, line: f64, color: [u8; 4], alpha: f64) {
    let (left, top) = (x.round(), y.round());
    let (right, bottom) = ((x + width).round(), (y + height).round());
    self.line(left, top, right, top, line, color, alpha);
    self.line(right, top, right, bottom, line, color, alpha);
    self.line(right, bottom, left, bottom, line, color, alpha);
    self.line(left, bottom, left, top, line, color, alpha);
  }

  /// Отрезок: пиксели берутся целыми (Брезенхем), а кисть кладёт квадрат со стороной в толщину линии. Так рисует
  /// пиксельная графика: без сглаживания, зато крест или кольцо на мини-карте видно всегда.
  pub fn line(&mut self, x0: f64, y0: f64, x1: f64, y1: f64, line: f64, color: [u8; 4], alpha: f64) {
    let brush = (line.round() as i64).max(1);
    let half = brush / 2;
    let (mut x, mut y) = (x0.round() as i64, y0.round() as i64);
    let (end_x, end_y) = (x1.round() as i64, y1.round() as i64);
    let (dx, dy) = ((end_x - x).abs(), -(end_y - y).abs());
    let (step_x, step_y) = (if x < end_x { 1 } else { -1 }, if y < end_y { 1 } else { -1 });
    let mut error = dx + dy;
    loop {
      for offset_y in -half..=(brush - 1 - half) {
        for offset_x in -half..=(brush - 1 - half) {
          self.blend(x + offset_x, y + offset_y, color, alpha);
        }
      }
      if x == end_x && y == end_y {
        break;
      }
      let twice = 2 * error;
      if twice >= dy {
        error += dy;
        x += step_x;
      }
      if twice <= dx {
        error += dx;
        y += step_y;
      }
    }
  }

  /// Обводка пути: путь приходит числами (`Float32Array`) тройками — вид точки, x, y. Вид `0` открывает подпуть
  /// (`moveTo`), вид `1` продолжает его (`lineTo`). Дуги обёртка дробит на отрезки сама (см. `js/prelude.js`):
  /// растеризатору довольно линий — как и пиксельной графике.
  pub fn stroke_path(&mut self, path: &[f64], line: f64, color: [u8; 4], alpha: f64) {
    let mut previous: Option<(f64, f64)> = None;
    for step in path.chunks_exact(3) {
      let (x, y) = (step[1], step[2]);
      // Начало подпути само по себе не рисуется: линия пойдёт от него к следующей точке — как в браузере.
      if step[0] == 0.0 {
        previous = Some((x, y));
        continue;
      }
      if let Some((from_x, from_y)) = previous {
        self.line(from_x, from_y, x, y, line, color, alpha);
      }
      previous = Some((x, y));
    }
  }

  /// Заливка пути: подпуть обводится, а нутро закрашивается построчно — так залит круг сигнала (`arc`).
  /// Подпути заливаются по очереди, правило то же, что у браузера по умолчанию: чётность пересечений.
  pub fn fill_path(&mut self, path: &[f64], color: [u8; 4], alpha: f64) {
    let mut outline: Vec<(f64, f64)> = Vec::new();
    for step in path.chunks_exact(3) {
      if step[0] == 0.0 {
        fill_outline(self, &mut outline, color, alpha);
      }
      outline.push((step[1], step[2]));
    }
    fill_outline(self, &mut outline, color, alpha);
  }

  /// Кладёт пиксели `ImageData` в холст. Как `putImageData` в браузере, содержимое заменяется, а не смешивается:
  /// на этом стоит мини-карта — прозрачные пиксели за краем карты должны стирать прежнее, а не размываться с ним.
  pub fn put_image_data(&mut self, x: i64, y: i64, width: usize, height: usize, data: &[u8]) {
    for row in 0..height {
      for column in 0..width {
        let from = (row * width + column) * 4;
        if from + 4 > data.len() {
          return;
        }
        if let Some(pixel) = self.at(x + column as i64, y + row as i64) {
          pixel.copy_from_slice(&data[from..from + 4]);
        }
      }
    }
  }

  /// Читает прямоугольник холста: как `getImageData` — чего нет за холстом, то прозрачно.
  pub fn get_image_data(&self, x: i64, y: i64, width: usize, height: usize) -> Vec<u8> {
    let mut out = vec![0u8; width * height * 4];
    for row in 0..height {
      let py = y + row as i64;
      if py < 0 || py >= self.height as i64 {
        continue;
      }
      for column in 0..width {
        let px = x + column as i64;
        if px < 0 || px >= self.width as i64 {
          continue;
        }
        let from = (py as usize * self.width + px as usize) * 4;
        let to = (row * width + column) * 4;
        out[to..to + 4].copy_from_slice(&self.pixels[from..from + 4]);
      }
    }
    out
  }

  /// Переносит картинку с другого холста — `drawImage` в браузере. Сглаживание включается флагом
  /// (`imageSmoothingEnabled`): портреты увеличивают целым числом и просят пиксели как есть, а уменьшают сглаженно.
  pub fn draw_image(
    &mut self,
    source: &Sheet,
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
    if sw <= 0.0 || sh <= 0.0 || dw <= 0.0 || dh <= 0.0 {
      return;
    }
    let (left, top) = (dx.round() as i64, dy.round() as i64);
    let (right, bottom) = ((dx + dw).round() as i64, (dy + dh).round() as i64);
    for py in top..bottom {
      for px in left..right {
        // Точка источника считается по центрам пикселей: при переносе один к одному берутся те же самые пиксели.
        let u = sx + (px as f64 + 0.5 - dx) * sw / dw - 0.5;
        let v = sy + (py as f64 + 0.5 - dy) * sh / dh - 0.5;
        let color = if smooth { source.bilinear(u, v) } else { source.nearest(u, v) };
        self.blend(px, py, color, alpha);
      }
    }
  }

  /// Ближайший пиксель: так переносят значки и карту — без размытия.
  fn nearest(&self, u: f64, v: f64) -> [u8; 4] {
    self.clamped(u.round() as i64, v.round() as i64)
  }

  /// Четыре соседних пикселя с весами: так уменьшают картинку (`imageSmoothingQuality: 'high'` в игре).
  fn bilinear(&self, u: f64, v: f64) -> [u8; 4] {
    let (x0, y0) = (u.floor(), v.floor());
    let (fx, fy) = (u - x0, v - y0);
    let mut out = [0.0f64; 4];
    for (step_x, step_y, weight) in [
      (0.0, 0.0, (1.0 - fx) * (1.0 - fy)),
      (1.0, 0.0, fx * (1.0 - fy)),
      (0.0, 1.0, (1.0 - fx) * fy),
      (1.0, 1.0, fx * fy),
    ] {
      if weight <= 0.0 {
        continue;
      }
      let color = self.clamped(x0 as i64 + step_x as i64, y0 as i64 + step_y as i64);
      for channel in 0..4 {
        out[channel] += color[channel] as f64 * weight;
      }
    }
    [out[0] as u8, out[1] as u8, out[2] as u8, out[3] as u8]
  }

  /// Пиксель с краю холста тянется: как у браузера, край не уходит в прозрачность при сглаживании.
  fn clamped(&self, x: i64, y: i64) -> [u8; 4] {
    if self.width == 0 || self.height == 0 {
      return CLEAR;
    }
    let x = x.clamp(0, self.width as i64 - 1) as usize;
    let y = y.clamp(0, self.height as i64 - 1) as usize;
    let at = (y * self.width + x) * 4;
    [self.pixels[at], self.pixels[at + 1], self.pixels[at + 2], self.pixels[at + 3]]
  }
}

/// Заливка собранного подпути: строки перебираются между парами пересечений рёбер — правило чётности.
fn fill_outline(sheet: &mut Sheet, outline: &mut Vec<(f64, f64)>, color: [u8; 4], alpha: f64) {
  if outline.len() < 3 {
    outline.clear();
    return;
  }
  let top = outline.iter().map(|(_, y)| *y).fold(f64::INFINITY, f64::min);
  let bottom = outline.iter().map(|(_, y)| *y).fold(f64::NEG_INFINITY, f64::max);
  for py in (top.floor() as i64)..=(bottom.ceil() as i64) {
    let center = py as f64 + 0.5;
    let mut crossings: Vec<f64> = Vec::new();
    for index in 0..outline.len() {
      let (x0, y0) = outline[index];
      let (x1, y1) = outline[(index + 1) % outline.len()];
      if (y0 <= center) == (y1 <= center) {
        continue;
      }
      crossings.push(x0 + (center - y0) / (y1 - y0) * (x1 - x0));
    }
    crossings.sort_by(f64::total_cmp);
    for pair in crossings.chunks_exact(2) {
      let (left, right) = (pair[0].round() as i64, pair[1].round() as i64);
      for px in left..right {
        sheet.blend(px, py, color, alpha);
      }
    }
  }
  outline.clear();
}

impl Sheet {
  /// Картинка холста в PNG: её ждёт `toDataURL` (скины, значки, портреты — всё идёт в стили и в `<img>`).
  ///
  /// Сжатие — «как есть»: полосы ложатся в поток без упаковки. Картинки здесь крошечные (скин 12×12, значок 8×8,
  /// портрет 32×32), и упаковка сжатия отняла бы больше, чем дала: в окно пришлось бы тащить ещё и его.
  pub fn to_png(&self) -> Vec<u8> {
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a];
    let mut header = Vec::new();
    header.extend_from_slice(&(self.width as u32).to_be_bytes());
    header.extend_from_slice(&(self.height as u32).to_be_bytes());
    // Восемь бит на канал, цвет с альфой, без сжатия и черезстрочности — как отдаёт холст браузера.
    header.extend_from_slice(&[8, 6, 0, 0, 0]);
    chunk(&mut out, b"IHDR", &header);
    let mut raw = Vec::with_capacity(self.height * (self.width * 4 + 1));
    for row in 0..self.height {
      // Перед полосой — её фильтр; нулевой значит «без преобразования».
      raw.push(0);
      raw.extend_from_slice(&self.pixels[row * self.width * 4..(row + 1) * self.width * 4]);
    }
    chunk(&mut out, b"IDAT", &zlib(&raw));
    chunk(&mut out, b"IEND", &[]);
    out
  }
}

/// Кусок PNG: длина, вид, данные и контрольная сумма — как требует формат.
fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
  out.extend_from_slice(&(data.len() as u32).to_be_bytes());
  let start = out.len();
  out.extend_from_slice(kind);
  out.extend_from_slice(data);
  let sum = crc32(&out[start..]);
  out.extend_from_slice(&sum.to_be_bytes());
}

/// Поток zlib без упаковки: заголовок, полосы «как есть» и контрольная сумма потока.
fn zlib(raw: &[u8]) -> Vec<u8> {
  let mut out = vec![0x78, 0x01];
  let mut rest = raw;
  loop {
    let take = rest.len().min(0xffff);
    let last = rest.len() <= 0xffff;
    out.push(if last { 1 } else { 0 });
    out.extend_from_slice(&(take as u16).to_le_bytes());
    out.extend_from_slice(&(!(take as u16)).to_le_bytes());
    out.extend_from_slice(&rest[..take]);
    rest = &rest[take..];
    if last {
      break;
    }
  }
  out.extend_from_slice(&adler32(raw).to_be_bytes());
  out
}

fn adler32(data: &[u8]) -> u32 {
  const MOD: u32 = 65521;
  let (mut low, mut high) = (1u32, 0u32);
  for byte in data {
    low = (low + *byte as u32) % MOD;
    high = (high + low) % MOD;
  }
  (high << 16) | low
}

fn crc32(data: &[u8]) -> u32 {
  let mut crc = 0xffff_ffffu32;
  for byte in data {
    crc ^= *byte as u32;
    for _ in 0..8 {
      crc = if crc & 1 != 0 { (crc >> 1) ^ 0xedb8_8320 } else { crc >> 1 };
    }
  }
  !crc
}

/// base64: `toDataURL` отдаёт картинку текстом, а этим её читают и стили, и `<img>`.
pub fn base64(data: &[u8]) -> String {
  const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
  for part in data.chunks(3) {
    let packed = (part[0] as u32) << 16 | (*part.get(1).unwrap_or(&0) as u32) << 8 | *part.get(2).unwrap_or(&0) as u32;
    for shift in [18, 12, 6, 0] {
      out.push(ALPHABET[(packed >> shift & 0x3f) as usize] as char);
    }
    // Хвост короче трёх байт добирается знаками «=» — так его читает и браузер.
    if part.len() < 3 {
      out.pop();
      out.push('=');
    }
    if part.len() < 2 {
      out.pop();
      out.push('=');
    }
  }
  out
}
