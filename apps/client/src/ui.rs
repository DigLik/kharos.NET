//! Немедленный интерфейс (по rwgpu): прямоугольники, текст fontdue, кнопки, значки видов. Всё — в один `Batch`
//! треугольников; отрезки батча помнят, чем их красить: шрифтовым атласом (альфа) или атласом вида (значок с палитрой
//! стороны). Тот же батч в координатах мира — слой поверх земли (рамка выделения, круги, частицы).

use std::collections::HashMap;

#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct Vertex {
  pub pos: [f32; 2],
  pub uv: [f32; 2],
  pub color: [u8; 4],
}

/// Чем красить отрезок батча: шрифтовой атлас или атлас обликов с палитрой стороны team.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Paint {
  Font,
  Sprite { team: usize },
}

#[derive(Default)]
pub struct Batch {
  pub vertices: Vec<Vertex>,
  pub indices: Vec<u32>,
  /// Отрезки индексов: с какого начинается и чем красить.
  pub segments: Vec<(usize, Paint)>,
}

/// Белый угол шрифтового атласа — для сплошной заливки.
pub const WHITE_UV: [f32; 2] = [2.0 / ATLAS_SIZE as f32, 2.0 / ATLAS_SIZE as f32];
pub const ATLAS_SIZE: u32 = 1024;

impl Batch {
  pub fn clear(&mut self) {
    self.vertices.clear();
    self.indices.clear();
    self.segments.clear();
  }

  fn paint(&mut self, paint: Paint) {
    if self.segments.last().map(|segment| segment.1) != Some(paint) {
      self.segments.push((self.indices.len(), paint));
    }
  }

  /// Четырёхугольник по углам по часовой: a, b, c, d.
  pub fn quad_points(&mut self, points: [[f32; 2]; 4], uv: [f32; 4], color: [u8; 4], paint: Paint) {
    self.paint(paint);
    let base = self.vertices.len() as u32;
    let uvs = [[uv[0], uv[1]], [uv[2], uv[1]], [uv[2], uv[3]], [uv[0], uv[3]]];
    for (pos, uv) in points.into_iter().zip(uvs) {
      self.vertices.push(Vertex { pos, uv, color });
    }
    self.indices.extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
  }

  pub fn quad(&mut self, min: [f32; 2], max: [f32; 2], uv: [f32; 4], color: [u8; 4], paint: Paint) {
    self.quad_points([[min[0], min[1]], [max[0], min[1]], [max[0], max[1]], [min[0], max[1]]], uv, color, paint);
  }

  pub fn rect(&mut self, min: [f32; 2], max: [f32; 2], color: [u8; 4]) {
    self.quad(min, max, [WHITE_UV[0], WHITE_UV[1], WHITE_UV[0], WHITE_UV[1]], color, Paint::Font);
  }

  /// Отрезок толщиной width.
  pub fn line(&mut self, a: [f32; 2], b: [f32; 2], width: f32, color: [u8; 4]) {
    let (dx, dy) = (b[0] - a[0], b[1] - a[1]);
    let length = (dx * dx + dy * dy).sqrt().max(1e-6);
    let (nx, ny) = (-dy / length * width / 2.0, dx / length * width / 2.0);
    let uv = [WHITE_UV[0], WHITE_UV[1], WHITE_UV[0], WHITE_UV[1]];
    self.quad_points([[a[0] + nx, a[1] + ny], [b[0] + nx, b[1] + ny], [b[0] - nx, b[1] - ny], [a[0] - nx, a[1] - ny]], uv, color, Paint::Font);
  }

  /// Выпуклый многоугольник веером.
  pub fn fan(&mut self, points: &[[f32; 2]], color: [u8; 4]) {
    self.paint(Paint::Font);
    let base = self.vertices.len() as u32;
    for &pos in points {
      self.vertices.push(Vertex { pos, uv: WHITE_UV, color });
    }
    for i in 1..(points.len() as u32).saturating_sub(1) {
      self.indices.extend_from_slice(&[base, base + i, base + i + 1]);
    }
  }

  /// Окружность: заливка (width = None) или кольцо толщиной width.
  pub fn circle(&mut self, center: [f32; 2], radius: f32, width: Option<f32>, color: [u8; 4], segments: usize) {
    let point = |i: usize, r: f32| {
      let angle = i as f32 / segments as f32 * std::f32::consts::TAU;
      [center[0] + angle.cos() * r, center[1] + angle.sin() * r]
    };
    match width {
      None => {
        let points: Vec<[f32; 2]> = (0..segments).map(|i| point(i, radius)).collect();
        self.fan(&points, color);
      }
      Some(width) => {
        let uv = [WHITE_UV[0], WHITE_UV[1], WHITE_UV[0], WHITE_UV[1]];
        for i in 0..segments {
          let (outer, inner) = (radius + width / 2.0, (radius - width / 2.0).max(0.0));
          self.quad_points([point(i, outer), point(i + 1, outer), point(i + 1, inner), point(i, inner)], uv, color, Paint::Font);
        }
      }
    }
  }
}

/// Шрифтовой атлас R8; левый верхний угол 4 × 4 — белый, для заливки.
pub struct FontAtlas {
  pub pixels: Vec<u8>,
  /// Растёт с каждой правкой: рендер знает, когда перезалить.
  pub generation: u64,
}

impl FontAtlas {
  fn new() -> Self {
    let mut atlas = Self { pixels: vec![0; (ATLAS_SIZE * ATLAS_SIZE) as usize], generation: 1 };
    atlas.clear();
    atlas
  }

  fn clear(&mut self) {
    self.pixels.fill(0);
    for y in 0..4 {
      for x in 0..4 {
        self.pixels[(y * ATLAS_SIZE + x) as usize] = 255;
      }
    }
    self.generation += 1;
  }
}

#[derive(Clone, Copy)]
struct Glyph {
  uv: [f32; 4],
  size: [f32; 2],
  offset: [f32; 2],
  advance: f32,
}

/// Текст fontdue: глифы растеризуются по требованию в физических пикселях.
pub struct Font {
  font: fontdue::Font,
  pub atlas: FontAtlas,
  glyphs: HashMap<(char, u16), Glyph>,
  cursor: [u32; 2],
  row: u32,
  scale: f32,
}

impl Font {
  pub fn new(bytes: &[u8]) -> Result<Self, String> {
    let font = fontdue::Font::from_bytes(bytes, fontdue::FontSettings::default()).map_err(|error| format!("шрифт: {error}"))?;
    Ok(Self { font, atlas: FontAtlas::new(), glyphs: HashMap::new(), cursor: [6, 0], row: 4, scale: 1.0 })
  }

  pub fn set_scale(&mut self, scale: f32) {
    if (scale - self.scale).abs() > 0.001 {
      self.scale = scale;
      self.reset();
    }
  }

  fn reset(&mut self) {
    self.glyphs.clear();
    self.atlas.clear();
    self.cursor = [6, 0];
    self.row = 4;
  }

  fn px(&self, size: f32) -> u16 {
    (size * self.scale).round().max(1.0) as u16
  }

  fn glyph(&mut self, char: char, px: u16) -> Glyph {
    if let Some(glyph) = self.glyphs.get(&(char, px)) {
      return *glyph;
    }
    let (metrics, bitmap) = self.font.rasterize(char, px as f32);
    let (w, h) = (metrics.width as u32, metrics.height as u32);
    if self.cursor[0] + w + 1 > ATLAS_SIZE {
      self.cursor = [0, self.cursor[1] + self.row + 1];
      self.row = 0;
    }
    if self.cursor[1] + h + 1 > ATLAS_SIZE {
      self.reset();
      self.cursor = [0, 5];
    }
    let [x, y] = self.cursor;
    for row in 0..h {
      let to = ((y + row) * ATLAS_SIZE + x) as usize;
      let from = (row * w) as usize;
      self.atlas.pixels[to..to + w as usize].copy_from_slice(&bitmap[from..from + w as usize]);
    }
    self.atlas.generation += 1;
    self.cursor[0] += w + 1;
    self.row = self.row.max(h);
    let size = ATLAS_SIZE as f32;
    let glyph = Glyph { uv: [x as f32 / size, y as f32 / size, (x + w) as f32 / size, (y + h) as f32 / size], size: [w as f32, h as f32], offset: [metrics.xmin as f32, -(metrics.ymin as f32 + h as f32)], advance: metrics.advance_width };
    self.glyphs.insert((char, px), glyph);
    glyph
  }

  pub fn line_height(&self, size: f32) -> f32 {
    let px = self.px(size);
    self.font.horizontal_line_metrics(px as f32).map_or(size * 1.2, |metrics| metrics.new_line_size / self.scale)
  }

  pub fn width(&mut self, text: &str, size: f32) -> f32 {
    let px = self.px(size);
    text.chars().map(|char| self.glyph(char, px).advance).sum::<f32>() / self.scale
  }

  /// Строка с левым верхним углом в pos; возвращает ширину.
  pub fn draw(&mut self, batch: &mut Batch, pos: [f32; 2], size: f32, color: [u8; 4], text: &str) -> f32 {
    let px = self.px(size);
    let scale = self.scale;
    let ascent = self.font.horizontal_line_metrics(px as f32).map_or(px as f32 * 0.8, |metrics| metrics.ascent);
    let origin = (pos[0] * scale).round();
    let baseline = (pos[1] * scale + ascent).round();
    let mut pen = 0.0f32;
    for char in text.chars() {
      let glyph = self.glyph(char, px);
      if glyph.size[0] > 0.0 {
        let x = origin + pen.round() + glyph.offset[0];
        let y = baseline + glyph.offset[1];
        batch.quad([x / scale, y / scale], [(x + glyph.size[0]) / scale, (y + glyph.size[1]) / scale], glyph.uv, color, Paint::Font);
      }
      pen += glyph.advance;
    }
    pen / scale
  }
}

/// Ответ элемента на указатель.
#[derive(Clone, Copy, Debug, Default)]
pub struct Response {
  pub hovered: bool,
  pub down: bool,
  pub clicked: bool,
}

/// Состояние немедленного интерфейса между кадрами.
pub struct Ui {
  pub batch: Batch,
  pub font: Font,
  pointer: Option<[f32; 2]>,
  down: bool,
  pressed: bool,
  released: bool,
  active: Option<u64>,
  /// Указатель над панелью или элементом в этом кадре — миру он не достаётся.
  pub captured: bool,
  /// То же в прошлом кадре: управление идёт после панелей и смотрит на него.
  pub was_captured: bool,
}

pub fn id(name: &str) -> u64 {
  name.bytes().fold(0xcbf29ce484222325, |hash, byte| (hash ^ byte as u64).wrapping_mul(0x100000001b3))
}

impl Ui {
  pub fn new(font: Font) -> Self {
    Self { batch: Batch::default(), font, pointer: None, down: false, pressed: false, released: false, active: None, captured: false, was_captured: false }
  }

  pub fn begin(&mut self, input: &crate::input::Input) {
    self.batch.clear();
    self.font.set_scale(input.scale);
    self.pointer = input.pointer;
    self.down = input.down[0];
    self.pressed = input.pressed[0];
    self.released = input.released[0];
    self.was_captured = self.captured;
    self.captured = false;
  }

  pub fn end(&mut self) {
    if !self.down {
      self.active = None;
    }
  }

  fn inside(&self, rect: [f32; 4]) -> bool {
    self.pointer.is_some_and(|[x, y]| x >= rect[0] && x < rect[0] + rect[2] && y >= rect[1] && y < rect[1] + rect[3])
  }

  pub fn interact(&mut self, id: u64, rect: [f32; 4]) -> Response {
    let hovered = self.inside(rect);
    self.captured |= hovered;
    let mut pressed = false;
    if hovered && self.pressed && self.active.is_none() {
      self.active = Some(id);
      pressed = true;
    }
    let active = self.active == Some(id);
    Response { hovered, down: active && (self.down || pressed), clicked: active && self.released && hovered }
  }

  /// Панель: заливка, и указатель над ней миру не достаётся.
  pub fn panel(&mut self, rect: [f32; 4], color: [u8; 4]) {
    self.batch.rect([rect[0], rect[1]], [rect[0] + rect[2], rect[1] + rect[3]], color);
    if self.inside(rect) {
      self.captured = true;
    }
  }

  pub fn outline(&mut self, rect: [f32; 4], width: f32, color: [u8; 4]) {
    let [x, y, w, h] = rect;
    self.batch.rect([x, y], [x + w, y + width], color);
    self.batch.rect([x, y + h - width], [x + w, y + h], color);
    self.batch.rect([x, y + width], [x + width, y + h - width], color);
    self.batch.rect([x + w - width, y + width], [x + w, y + h - width], color);
  }

  pub fn text(&mut self, pos: [f32; 2], size: f32, color: [u8; 4], text: &str) -> f32 {
    self.font.draw(&mut self.batch, pos, size, color, text)
  }
}
