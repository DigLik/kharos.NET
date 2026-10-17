//! Замер рендера на стеке клиента rwgpu (docs/content.md, §6): окно winit, контекст glutin (GL 3.3 core), рисование
//! glow. Юниты — багги из `base/units/buggy.luau`: его чертёж растеризуется в атлас (64 направления × 2 фазы), ходовая
//! `base/moves/wheels.luau` двигает всех каждый кадр, а рисуются все одним вызовом с инстансингом. Вертикальной
//! синхронизации нет: кадр идёт сразу за кадром. Через SECONDS секунд печатает время кадра по частям и закрывается.
//!
//!   cargo run -p content-bench --release --bin render [юнитов]

use content_bench::{bytes, load, new_lua, Pixmap, TURN};
use glow::HasContext as _;
use glutin::config::ConfigTemplateBuilder;
use glutin::context::{ContextApi, ContextAttributesBuilder, GlProfile, NotCurrentGlContext as _, PossiblyCurrentContext, Version};
use glutin::display::{GetGlDisplay as _, GlDisplay as _};
use glutin::surface::{GlSurface as _, Surface, SwapInterval, WindowSurface};
use glutin_winit::{DisplayBuilder, GlWindow as _};
use mlua::{Buffer, Function, Lua, Table};
use raw_window_handle::HasWindowHandle as _;
use std::time::Instant;
use winit::application::ApplicationHandler;
use winit::dpi::LogicalSize;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, EventLoop};
use winit::window::{Window, WindowId};

/// Поле в тайлах: всё целиком на экране, юнит — два тайла, как в игре.
const WIDTH: f64 = 160.0;
const HEIGHT: f64 = 100.0;
const DIRECTIONS: usize = 64;
const PHASES: usize = 2;
/// Атлас: кадры 32×32 сеткой 16 × 8.
const COLUMNS: usize = 16;
const SECONDS: f64 = 10.0;
const WARMUP: f64 = 2.0;

const VS: &str = r#"#version 330 core
layout(location = 0) in vec2 corner;
layout(location = 1) in vec3 unit;
uniform vec2 u_field;
out vec2 v_uv;
void main() {
  vec2 at = unit.xy + (corner - 0.5) * 2.0;
  vec2 ndc = at / u_field * 2.0 - 1.0;
  gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
  float frame = unit.z;
  vec2 cell = vec2(mod(frame, 16.0), floor(frame / 16.0));
  v_uv = (cell + corner) / vec2(16.0, 8.0);
}
"#;

const FS: &str = r#"#version 330 core
in vec2 v_uv;
uniform sampler2D u_atlas;
out vec4 color;
void main() {
  vec4 texel = texture(u_atlas, v_uv);
  if (texel.a < 0.5) discard;
  color = texel;
}
"#;

/// Чертёж вида на все направления и фазы — атлас RGBA. Метка тона в старшем байте цвета — маска цвета стороны: здесь
/// она просто снимается.
fn atlas(lua: &Lua, look: &Table) -> mlua::Result<(Vec<u8>, usize, usize)> {
  let draw: Function = look.get("draw")?;
  let frame: usize = look.get("frame")?;
  let rows = DIRECTIONS * PHASES / COLUMNS;
  let (width, height) = (COLUMNS * frame, rows * frame);
  let mut pixels = vec![0u8; width * height * 4];
  for phase in 0..PHASES {
    for direction in 0..DIRECTIONS {
      let canvas = lua.create_userdata(Pixmap { size: frame, data: vec![0; frame * frame] })?;
      draw.call::<()>((&canvas, direction as f64 / DIRECTIONS as f64 * TURN, phase))?;
      let index = phase * DIRECTIONS + direction;
      let (left, top) = (index % COLUMNS * frame, index / COLUMNS * frame);
      for (at, &color) in canvas.borrow::<Pixmap>()?.data.iter().enumerate() {
        if color == 0 {
          continue;
        }
        let offset = ((top + at / frame) * width + left + at % frame) * 4;
        pixels[offset..offset + 4].copy_from_slice(&[(color >> 16) as u8, (color >> 8) as u8, color as u8, 255]);
      }
    }
  }
  Ok((pixels, width, height))
}

/// Мир замера: машина Luau, ходовая и столбцы юнитов буферами.
struct Sim {
  _lua: Lua,
  system: Function,
  ctx: Table,
  x: Buffer,
  y: Buffer,
  facing: Buffer,
  units: usize,
  tick: u64,
}

impl Sim {
  fn new(units: usize) -> mlua::Result<(Self, (Vec<u8>, usize, usize))> {
    let lua = new_lua(true)?;
    let wheels = load(&lua, "moves/wheels.luau")?;
    let buggy = load(&lua, "units/buggy.luau")?;
    let data: Table = buggy.get("data")?;
    let art = atlas(&lua, &buggy.get("look")?)?;
    let spread = |i: usize, salt: usize, size: f64| ((i * 2654435761 + salt) % size as usize) as f64 + 0.5;
    let column = |salt: usize, size: f64| lua.create_buffer(bytes(&(0..units).map(|i| spread(i, salt, size)).collect::<Vec<_>>()));
    let (x, y, tx, ty) = (column(1, WIDTH)?, column(7, HEIGHT)?, column(13, WIDTH)?, column(29, HEIGHT)?);
    let facing = lua.create_buffer(vec![0u8; units * 8])?;
    let ctx = lua.create_table()?;
    for (name, buffer) in [("x", &x), ("y", &y), ("facing", &facing), ("tx", &tx), ("ty", &ty)] {
      ctx.set(name, buffer.clone())?;
    }
    ctx.set("n", units)?;
    ctx.set("speed", data.get::<f64>("speed")?)?;
    ctx.set("turn", data.get::<f64>("turn")?)?;
    ctx.set("dt", 0.05)?;
    ctx.set("width", WIDTH)?;
    ctx.set("height", HEIGHT)?;
    let system: Function = wheels.get::<Table>("systems")?.get("move")?;
    Ok((Self { _lua: lua, system, ctx, x, y, facing, units, tick: 0 }, art))
  }

  fn step(&mut self) {
    self.ctx.set("tick", self.tick).unwrap();
    self.system.call::<()>(&self.ctx).unwrap();
    self.tick += 1;
  }

  /// Что рисовать: у юнита место и кадр атласа — по направлению и фазе.
  fn instances(&self, out: &mut Vec<f32>) {
    let (x, y, facing) = (self.x.to_vec(), self.y.to_vec(), self.facing.to_vec());
    let read = |column: &[u8], i: usize| f64::from_le_bytes(column[i * 8..i * 8 + 8].try_into().unwrap());
    out.clear();
    let phase = (self.tick / 4 % PHASES as u64) as usize;
    for i in 0..self.units {
      let direction = (read(&facing, i) / TURN * DIRECTIONS as f64).round().rem_euclid(DIRECTIONS as f64) as usize;
      out.extend_from_slice(&[read(&x, i) as f32, read(&y, i) as f32, (phase * DIRECTIONS + direction) as f32]);
    }
  }
}

struct Gpu {
  window: Window,
  surface: Surface<WindowSurface>,
  context: PossiblyCurrentContext,
  gl: glow::Context,
  instances: glow::Buffer,
}

#[derive(Default)]
struct Times {
  frames: u32,
  sim: f64,
  build: f64,
  draw: f64,
  swap: f64,
  total: f64,
}

struct Bench {
  units: usize,
  gpu: Option<Gpu>,
  sim: Option<Sim>,
  scratch: Vec<f32>,
  started: Option<Instant>,
  last: Option<Instant>,
  times: Times,
  /// Замер кончился: выход попрошен, а кадры ещё могут прийти.
  done: bool,
}

impl Bench {
  fn frame(&mut self, event_loop: &ActiveEventLoop) {
    if self.done {
      return;
    }
    let (Some(gpu), Some(sim)) = (&self.gpu, &mut self.sim) else { return };
    let start = Instant::now();
    sim.step();
    let simulated = Instant::now();
    sim.instances(&mut self.scratch);
    let built = Instant::now();
    let size = gpu.window.inner_size();
    unsafe {
      gpu.gl.viewport(0, 0, size.width as i32, size.height as i32);
      gpu.gl.clear_color(0.16, 0.13, 0.10, 1.0);
      gpu.gl.clear(glow::COLOR_BUFFER_BIT);
      gpu.gl.bind_buffer(glow::ARRAY_BUFFER, Some(gpu.instances));
      let raw = std::slice::from_raw_parts(self.scratch.as_ptr() as *const u8, self.scratch.len() * 4);
      gpu.gl.buffer_data_u8_slice(glow::ARRAY_BUFFER, raw, glow::STREAM_DRAW);
      gpu.gl.draw_arrays_instanced(glow::TRIANGLE_STRIP, 0, 4, sim.units as i32);
    }
    let drawn = Instant::now();
    gpu.surface.swap_buffers(&gpu.context).unwrap();
    let swapped = Instant::now();

    let started = *self.started.get_or_insert(start);
    let elapsed = start.duration_since(started).as_secs_f64();
    if elapsed > WARMUP {
      let ms = |from: Instant, to: Instant| to.duration_since(from).as_secs_f64() * 1000.0;
      let t = &mut self.times;
      t.frames += 1;
      t.sim += ms(start, simulated);
      t.build += ms(simulated, built);
      t.draw += ms(built, drawn);
      t.swap += ms(drawn, swapped);
      if let Some(last) = self.last {
        t.total += ms(last, start);
      }
    }
    self.last = Some(start);
    if elapsed > WARMUP + SECONDS {
      let t = &self.times;
      let n = t.frames as f64;
      let frame = t.total / (n - 1.0);
      println!("{} юнитов, окно {}×{} пикселей, {} кадров за {SECONDS} с", self.units, size.width, size.height, t.frames);
      println!("  кадр: {frame:.3} мс — {:.0} кадров в секунду", 1000.0 / frame);
      println!("  ход на Luau: {:.3} мс, сборка инстансов: {:.3} мс, отправка в GL: {:.3} мс, swap: {:.3} мс", t.sim / n, t.build / n, t.draw / n, t.swap / n);
      self.done = true;
      event_loop.exit();
    }
  }
}

impl ApplicationHandler for Bench {
  fn resumed(&mut self, event_loop: &ActiveEventLoop) {
    if self.gpu.is_some() {
      return;
    }
    let attributes = Window::default_attributes().with_title("Kharos: замер рендера").with_inner_size(LogicalSize::new(1280.0, 800.0));
    let (window, config) = DisplayBuilder::new().with_window_attributes(Some(attributes)).build(event_loop, ConfigTemplateBuilder::new(), |mut configs| configs.next().unwrap()).unwrap();
    let window = window.unwrap();
    let handle = window.window_handle().ok().map(|handle| handle.as_raw());
    let context_attributes = ContextAttributesBuilder::new().with_profile(GlProfile::Core).with_context_api(ContextApi::OpenGl(Some(Version::new(3, 3)))).build(handle);
    let display = config.display();
    let context = unsafe { display.create_context(&config, &context_attributes) }.unwrap();
    let surface_attributes = window.build_surface_attributes(Default::default()).unwrap();
    let surface = unsafe { display.create_window_surface(&config, &surface_attributes) }.unwrap();
    let context = context.make_current(&surface).unwrap();
    if let Err(error) = surface.set_swap_interval(&context, SwapInterval::DontWait) {
      eprintln!("вертикальную синхронизацию не выключить: {error}");
    }
    let gl = unsafe { glow::Context::from_loader_function_cstr(|name| display.get_proc_address(name)) };

    let (sim, (pixels, width, height)) = Sim::new(self.units).unwrap();
    let instances = unsafe {
      let program = gl.create_program().unwrap();
      for (kind, source) in [(glow::VERTEX_SHADER, VS), (glow::FRAGMENT_SHADER, FS)] {
        let shader = gl.create_shader(kind).unwrap();
        gl.shader_source(shader, source);
        gl.compile_shader(shader);
        assert!(gl.get_shader_compile_status(shader), "{}", gl.get_shader_info_log(shader));
        gl.attach_shader(program, shader);
      }
      gl.link_program(program);
      assert!(gl.get_program_link_status(program), "{}", gl.get_program_info_log(program));
      gl.use_program(Some(program));
      gl.uniform_2_f32(gl.get_uniform_location(program, "u_field").as_ref(), WIDTH as f32, HEIGHT as f32);

      let texture = gl.create_texture().unwrap();
      gl.bind_texture(glow::TEXTURE_2D, Some(texture));
      gl.tex_image_2d(glow::TEXTURE_2D, 0, glow::RGBA8 as i32, width as i32, height as i32, 0, glow::RGBA, glow::UNSIGNED_BYTE, glow::PixelUnpackData::Slice(Some(&pixels)));
      gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MIN_FILTER, glow::NEAREST as i32);
      gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MAG_FILTER, glow::NEAREST as i32);

      let vao = gl.create_vertex_array().unwrap();
      gl.bind_vertex_array(Some(vao));
      let corners = gl.create_buffer().unwrap();
      gl.bind_buffer(glow::ARRAY_BUFFER, Some(corners));
      let quad: [f32; 8] = [0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0];
      gl.buffer_data_u8_slice(glow::ARRAY_BUFFER, std::slice::from_raw_parts(quad.as_ptr() as *const u8, 32), glow::STATIC_DRAW);
      gl.enable_vertex_attrib_array(0);
      gl.vertex_attrib_pointer_f32(0, 2, glow::FLOAT, false, 8, 0);
      let instances = gl.create_buffer().unwrap();
      gl.bind_buffer(glow::ARRAY_BUFFER, Some(instances));
      gl.enable_vertex_attrib_array(1);
      gl.vertex_attrib_pointer_f32(1, 3, glow::FLOAT, false, 12, 0);
      gl.vertex_attrib_divisor(1, 1);
      instances
    };
    self.sim = Some(sim);
    self.gpu = Some(Gpu { window, surface, context, gl, instances });
  }

  fn window_event(&mut self, event_loop: &ActiveEventLoop, _: WindowId, event: WindowEvent) {
    match event {
      WindowEvent::CloseRequested => event_loop.exit(),
      WindowEvent::RedrawRequested => self.frame(event_loop),
      _ => {}
    }
  }

  fn about_to_wait(&mut self, _: &ActiveEventLoop) {
    if let Some(gpu) = &self.gpu {
      gpu.window.request_redraw();
    }
  }
}

fn main() {
  let units = std::env::args().nth(1).and_then(|count| count.parse().ok()).unwrap_or(10_000);
  let event_loop = EventLoop::new().unwrap();
  let mut bench = Bench { units, gpu: None, sim: None, scratch: Vec::new(), started: None, last: None, times: Times::default(), done: false };
  event_loop.run_app(&mut bench).unwrap();
}
