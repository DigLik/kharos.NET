//! Облик видов: чертёж `look.draw(canvas, угол, фаза)` растеризуется в атлас — кадр на каждое направление и фазу.
//! Холст — векторные фигуры в пикселях спрайта (16 на тайл), начало — центр; пиксель закрашивается, если в фигуру
//! попал его центр, — выходит пиксель-арт. Цвет стороны — метка тона (`canvas:team(тон)`): в атласе альфа 254 − тон,
//! шейдер перекрашивает такой пиксель палитрой игрока.

use mlua::{Function, Lua, Table, UserData, UserDataFields, UserDataMethods, Value};

pub const TURN: f64 = std::f64::consts::PI * 2.0;

/// Холст одного кадра. Цвет — 0xRRGGBB, у цвета стороны в старшем байте тон + 1.
pub struct Canvas {
  pub size: usize,
  pub data: Vec<u32>,
}

impl Canvas {
  fn fill(&mut self, color: u32, bounds: [f64; 4], inside: impl Fn(f64, f64) -> bool) {
    let half = self.size as f64 / 2.0;
    let [left, top, right, bottom] = bounds;
    let x0 = ((left + half).floor().max(0.0)) as usize;
    let y0 = ((top + half).floor().max(0.0)) as usize;
    let x1 = ((right + half).ceil().min(self.size as f64)) as usize;
    let y1 = ((bottom + half).ceil().min(self.size as f64)) as usize;
    for py in y0..y1 {
      for px in x0..x1 {
        if inside(px as f64 + 0.5 - half, py as f64 + 0.5 - half) {
          self.data[py * self.size + px] = color;
        }
      }
    }
  }
}

fn polygon_inside(points: &[(f64, f64)], x: f64, y: f64) -> bool {
  let mut inside = false;
  let mut j = points.len() - 1;
  for i in 0..points.len() {
    let (xi, yi) = points[i];
    let (xj, yj) = points[j];
    if (yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi {
      inside = !inside;
    }
    j = i;
  }
  inside
}

impl UserData for Canvas {
  fn add_fields<F: UserDataFields<Self>>(fields: &mut F) {
    fields.add_field_method_get("size", |_, canvas| Ok(canvas.size));
  }

  fn add_methods<M: UserDataMethods<Self>>(methods: &mut M) {
    methods.add_method_mut("circle", |_, canvas, (x, y, radius, color): (f64, f64, f64, u32)| {
      canvas.fill(color, [x - radius, y - radius, x + radius, y + radius], |px, py| (px - x).powi(2) + (py - y).powi(2) <= radius * radius);
      Ok(())
    });
    methods.add_method_mut("ring", |_, canvas, (x, y, radius, width, color): (f64, f64, f64, f64, u32)| {
      let (outer, inner) = (radius + width / 2.0, (radius - width / 2.0).max(0.0));
      canvas.fill(color, [x - outer, y - outer, x + outer, y + outer], |px, py| {
        let distance = (px - x).powi(2) + (py - y).powi(2);
        distance <= outer * outer && distance >= inner * inner
      });
      Ok(())
    });
    methods.add_method_mut("line", |_, canvas, (ax, ay, bx, by, width, color): (f64, f64, f64, f64, f64, u32)| {
      let (dx, dy) = (bx - ax, by - ay);
      let length = dx * dx + dy * dy;
      let half = width / 2.0;
      canvas.fill(color, [ax.min(bx) - half, ay.min(by) - half, ax.max(bx) + half, ay.max(by) + half], |px, py| {
        let t = if length > 0.0 { (((px - ax) * dx + (py - ay) * dy) / length).clamp(0.0, 1.0) } else { 0.0 };
        (px - ax - dx * t).powi(2) + (py - ay - dy * t).powi(2) <= half * half
      });
      Ok(())
    });
    // Прямоугольник с центром (x, y), размером w × h, повёрнутый на angle.
    methods.add_method_mut("rect", |_, canvas, (x, y, w, h, angle, color): (f64, f64, f64, f64, f64, u32)| {
      let (cos, sin) = (angle.cos(), angle.sin());
      let reach = (w * w + h * h).sqrt() / 2.0;
      canvas.fill(color, [x - reach, y - reach, x + reach, y + reach], |px, py| {
        let (dx, dy) = (px - x, py - y);
        let (u, v) = (dx * cos + dy * sin, -dx * sin + dy * cos);
        u.abs() <= w / 2.0 && v.abs() <= h / 2.0
      });
      Ok(())
    });
    // Многоугольник: { x1, y1, x2, y2, … }, чётно-нечётное правило.
    methods.add_method_mut("poly", |_, canvas, (coords, color): (Vec<f64>, u32)| {
      let points: Vec<(f64, f64)> = coords.chunks_exact(2).map(|pair| (pair[0], pair[1])).collect();
      if points.len() < 3 {
        return Ok(());
      }
      let left = points.iter().map(|p| p.0).fold(f64::MAX, f64::min);
      let right = points.iter().map(|p| p.0).fold(f64::MIN, f64::max);
      let top = points.iter().map(|p| p.1).fold(f64::MAX, f64::min);
      let bottom = points.iter().map(|p| p.1).fold(f64::MIN, f64::max);
      canvas.fill(color, [left, top, right, bottom], |px, py| polygon_inside(&points, px, py));
      Ok(())
    });
    methods.add_method_mut("pixel", |_, canvas, (x, y, color): (f64, f64, u32)| {
      let half = canvas.size as f64 / 2.0;
      let (px, py) = ((x + half).floor(), (y + half).floor());
      if px >= 0.0 && py >= 0.0 && (px as usize) < canvas.size && (py as usize) < canvas.size {
        let size = canvas.size;
        canvas.data[py as usize * size + px as usize] = color;
      }
      Ok(())
    });
    // Цвет стороны тона 0 (тёмный), 1 (основной) или 2 (светлый): перекрасит палитра игрока.
    methods.add_method("team", |_, _, tone: u32| Ok(0x5aa9ff + (tone.min(2) + 1) * 0x1000000));
    // Цвет темнее (k < 1) или светлее (k > 1).
    methods.add_function("shade", |_, (color, k): (u32, f64)| {
      let channel = |shift: u32| ((((color >> shift) & 0xff) as f64 * k).round().clamp(0.0, 255.0) as u32) << shift;
      Ok((color & 0xff000000) | channel(16) | channel(8) | channel(0))
    });
  }
}

/// Огни облика: фары (вдоль, поперёк, радиус — в пикселях спрайта), луч, свечения неподвижных (x, y, радиус).
#[derive(Clone, Debug, Default)]
pub struct Lights {
  pub lamps: Vec<[f32; 3]>,
  /// along, length, near, spread, level.
  pub beam: Option<[f32; 5]>,
  pub glows: Vec<[f32; 3]>,
}

/// Облик вида: атлас и что нужно рисовать и звучать.
pub struct Look {
  pub proto: u32,
  pub frame: usize,
  pub directions: usize,
  pub phases: usize,
  pub columns: usize,
  pub rows: usize,
  /// RGBA атласа: кадр (фаза · directions + направление) в ячейке по строкам.
  pub pixels: Vec<u8>,
  /// Кадров в секунду анимации; анимируется всегда или только на ходу.
  pub fps: f32,
  pub always: bool,
  pub lights: Lights,
  /// Таблица `look` — звук и прочее берутся из неё по ходу.
  pub table: Table,
  /// Направление кадра значка.
  pub icon: usize,
}

impl Look {
  pub fn size(&self) -> [i32; 2] {
    [(self.columns * self.frame) as i32, (self.rows * self.frame) as i32]
  }

  /// uv кадра в атласе: [u0, v0, u1, v1].
  pub fn uv(&self, cell: usize) -> [f32; 4] {
    let (column, row) = (cell % self.columns, cell / self.columns);
    let [w, h] = [self.columns as f32, self.rows as f32];
    [column as f32 / w, row as f32 / h, (column + 1) as f32 / w, (row + 1) as f32 / h]
  }

  /// Кадр направления angle (радианы) в фазе phase.
  pub fn cell(&self, angle: f64, phase: usize) -> usize {
    let direction = if self.directions <= 1 { 0 } else { (angle / TURN * self.directions as f64).round().rem_euclid(self.directions as f64) as usize };
    (phase % self.phases.max(1)) * self.directions + direction
  }
}

fn numbers<const N: usize>(table: &Table) -> mlua::Result<[f32; N]> {
  let mut out = [0.0; N];
  for (i, slot) in out.iter_mut().enumerate() {
    *slot = table.get::<f32>(i + 1)?;
  }
  Ok(out)
}

/// Растеризует облик вида proto (таблица вида с полем `look`).
pub fn build(lua: &Lua, proto: u32, look: Table) -> mlua::Result<Look> {
  let draw: Function = look.get("draw")?;
  let frame: usize = look.get::<Option<usize>>("frame")?.unwrap_or(32);
  let directions: usize = look.get::<Option<usize>>("directions")?.unwrap_or(1).max(1);
  let phases: usize = look.get::<Option<usize>>("phases")?.unwrap_or(1).max(1);
  let count = directions * phases;
  let columns = (count as f64).sqrt().ceil() as usize;
  let rows = count.div_ceil(columns);
  let width = columns * frame;
  let mut pixels = vec![0u8; width * rows * frame * 4];
  for phase in 0..phases {
    for direction in 0..directions {
      let canvas = lua.create_userdata(Canvas { size: frame, data: vec![0; frame * frame] })?;
      draw.call::<()>((&canvas, direction as f64 / directions as f64 * TURN, phase))?;
      let cell = phase * directions + direction;
      let (left, top) = (cell % columns * frame, cell / columns * frame);
      for (at, &color) in canvas.borrow::<Canvas>()?.data.iter().enumerate() {
        if color == 0 {
          continue;
        }
        let mark = color >> 24;
        let alpha = if mark == 0 { 255 } else { 255 - mark as u8 };
        let offset = ((top + at / frame) * width + left + at % frame) * 4;
        pixels[offset..offset + 4].copy_from_slice(&[(color >> 16) as u8, (color >> 8) as u8, color as u8, alpha]);
      }
    }
  }
  let mut lights = Lights::default();
  if let Some(table) = look.get::<Option<Table>>("lights")? {
    if let Some(lamps) = table.get::<Option<Vec<Table>>>("lamps")? {
      lights.lamps = lamps.iter().map(numbers::<3>).collect::<mlua::Result<_>>()?;
    }
    if let Some(glows) = table.get::<Option<Vec<Table>>>("glows")? {
      lights.glows = glows.iter().map(numbers::<3>).collect::<mlua::Result<_>>()?;
    }
    if let Some(beam) = table.get::<Option<Table>>("beam")? {
      let get = |key: &str, default: f32| beam.get::<Option<f32>>(key).map(|value| value.unwrap_or(default));
      lights.beam = Some([get("along", 8.0)?, get("length", 60.0)?, get("near", 4.0)?, get("spread", 0.28)?, get("level", 1.0)?]);
    }
  }
  let fps = look.get::<Option<f32>>("fps")?.unwrap_or(8.0);
  let always = look.get::<Option<String>>("animate")?.as_deref() == Some("always") || directions == 1;
  let icon = match look.get::<Value>("icon")? {
    Value::Table(table) => table.get::<Option<usize>>("direction")?.unwrap_or(0),
    _ => 0,
  };
  Ok(Look { proto, frame, directions, phases, columns, rows, pixels, fps, always, lights, table: look, icon })
}
