//! Нативная оболочка Kharos: окно, настоящий OpenGL и рантайм JavaScript вместо браузера.
//!
//! Игра не переписывается: её код на TypeScript идёт в рантайм как есть, а браузерный API заменяют три части —
//! обёртка `js/prelude.js` (холст, кадры, таймеры, состояние WebGL), привязки `src/gl` (настоящие вызовы драйвера)
//! и `src/web` (то, что в браузере даёт движок, а не страница). Собранная игра лежит рядом с привязками
//! (`js/game.bundle.js`) — её кладёт сборщик, см. README.

mod canvas;
mod engine;
mod gl;
mod modules;
mod web;

use std::fs;
use std::num::NonZeroU32;
use std::path::Path;
use std::time::Instant;

use anyhow::{anyhow, Result};
use deno_core::ModuleSpecifier;
use glutin::config::{Config, ConfigTemplateBuilder, GlConfig};
use glutin::context::{ContextApi, ContextAttributesBuilder, GlProfile, NotCurrentGlContext, PossiblyCurrentContext, Version};
use glutin::display::{GetGlDisplay, GlDisplay};
use glutin::surface::{GlSurface, Surface, SwapInterval, WindowSurface};
use glutin_winit::{DisplayBuilder, GlWindow};
use raw_window_handle::HasWindowHandle;
use winit::application::ApplicationHandler;
use winit::dpi::LogicalSize;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, EventLoop};
use winit::window::{Window, WindowAttributes, WindowId};

/// Что запускается в рантайме: собранная игра. Rust берёт её с диска, а не вшивает в двоичный файл: так правка в
/// игре видна без пересборки окна. Другой файл берётся переменной окружения — `KHAROS_SCRIPT=js/smoke.js cargo run`
/// запускает прежнюю проверку фундамента (см. README).
const GAME: &str = "js/game.bundle.js";

/// Адрес кода игры. Путь ищется сначала от текущего каталога (так и запускают `cargo run`), потом от каталога окна.
/// Отсюда же игра узнаёт свой `import.meta.url` — по нему она ищет то, что стоит рядом с ней.
fn game_url() -> Result<ModuleSpecifier> {
  let name = std::env::var("KHAROS_SCRIPT").unwrap_or_else(|_| GAME.to_string());
  let path = Path::new(&name);
  let path = fs::canonicalize(path)
    .or_else(|_| fs::canonicalize(Path::new(env!("CARGO_MANIFEST_DIR")).join(path)))
    .map_err(|error| anyhow!("код игры не найден ({name}): {error}; собрать его: bun run bundle:native"))?;
  ModuleSpecifier::from_file_path(&path).map_err(|_| anyhow!("путь игры не переводится в адрес: {}", path.display()))
}

fn main() -> Result<()> {
  let event_loop = EventLoop::new()?;
  let mut app = App::default();
  event_loop.run_app(&mut app)?;
  app.error.map_or(Ok(()), Err)
}

#[derive(Default)]
struct App {
  window: Option<Window>,
  surface: Option<Surface<WindowSurface>>,
  context: Option<PossiblyCurrentContext>,
  engine: Option<engine::Engine>,
  started: Option<Instant>,
  /// Ошибка, случившаяся уже во время работы: кадры после неё не идут, а сборка сообщает о ней на выходе.
  error: Option<anyhow::Error>,
}

impl App {
  /// Создаёт окно, контекст драйвера и рантайм с игрой.
  fn start(&mut self, event_loop: &ActiveEventLoop) -> Result<()> {
    let attributes = WindowAttributes::default()
      .with_title("Kharos")
      .with_inner_size(LogicalSize::new(1280.0, 720.0));
    let template = ConfigTemplateBuilder::new().with_alpha_size(8);
    // Берём конфигурацию с наибольшим числом сэмплов: картинку рисуют проходы, и лишнее сглаживание только мешает.
    let (window, config) = DisplayBuilder::new()
      .with_window_attributes(Some(attributes))
      .build(event_loop, template, pick_config)
      .map_err(|error| anyhow!("окно не создалось: {error}"))?;
    let window = window.ok_or_else(|| anyhow!("окно не создалось"))?;
    let display = config.display();
    let raw = window.window_handle()?.as_raw();

    // Игра написана для WebGL 2 — это OpenGL ES 3.0. На настольном драйвере берём ближайшую версию с профилем ядра,
    // а если её нет (так бывает в эмуляторах и на старых сборках), пробуем настоящий GLES.
    let desktop = ContextAttributesBuilder::new()
      .with_context_api(ContextApi::OpenGl(Some(Version::new(3, 3))))
      .with_profile(GlProfile::Core)
      .build(Some(raw));
    let portable = ContextAttributesBuilder::new().with_context_api(ContextApi::Gles(None)).build(Some(raw));
    let (context, desktop) = match unsafe { display.create_context(&config, &desktop) } {
      Ok(context) => (context, true),
      Err(_) => (
        unsafe { display.create_context(&config, &portable) }.map_err(|error| anyhow!("нет контекста OpenGL: {error}"))?,
        false,
      ),
    };

    let surface_attributes = window.build_surface_attributes(Default::default())?;
    let surface = unsafe { display.create_window_surface(&config, &surface_attributes)? };
    let context = context.make_current(&surface)?;
    let driver = unsafe { glow::Context::from_loader_function_cstr(|name| display.get_proc_address(name)) };
    // Показ ждёт вертикальную синхронизацию: игра считает кадры от времени, как и в браузере.
    let _ = surface.set_swap_interval(&context, SwapInterval::Wait(NonZeroU32::new(1).unwrap()));

    gl::install(driver, desktop);
    let mut engine = engine::Engine::new()?;
    engine.run(&game_url()?)?;
    let size = window.inner_size();
    engine.resize(size.width as f64, size.height as f64, window.scale_factor())?;

    self.window = Some(window);
    self.surface = Some(surface);
    self.context = Some(context);
    self.engine = Some(engine);
    self.started = Some(Instant::now());
    Ok(())
  }

  /// Один кадр: игра считает свой кадр, Rust показывает нарисованное.
  fn redraw(&mut self) -> Result<()> {
    let (Some(engine), Some(surface), Some(context), Some(started)) =
      (self.engine.as_mut(), self.surface.as_ref(), self.context.as_ref(), self.started.as_ref())
    else {
      return Ok(());
    };
    engine.frame(started.elapsed().as_secs_f64() * 1000.0)?;
    surface.swap_buffers(context)?;
    Ok(())
  }

  /// Размер окна изменился: о нём узнают и драйвер, и игра.
  fn resize(&mut self, width: u32, height: u32) {
    let (Some(surface), Some(context)) = (self.surface.as_ref(), self.context.as_ref()) else {
      return;
    };
    if let (Some(width), Some(height)) = (NonZeroU32::new(width), NonZeroU32::new(height)) {
      surface.resize(context, width, height);
      // Плотность берётся у окна: на экране с удвоенными пикселями игра рисует вдвое плотнее.
      let ratio = self.window.as_ref().map_or(1.0, |window| window.scale_factor());
      if let Some(engine) = self.engine.as_mut() {
        if let Err(error) = engine.resize(width.get() as f64, height.get() as f64, ratio) {
          self.error = Some(error);
        }
      }
    }
  }
}

impl ApplicationHandler for App {
  fn resumed(&mut self, event_loop: &ActiveEventLoop) {
    if self.window.is_some() {
      return;
    }
    if let Err(error) = self.start(event_loop) {
      self.error = Some(error);
      event_loop.exit();
      return;
    }
    if let Some(window) = self.window.as_ref() {
      window.request_redraw();
    }
  }

  fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
    match event {
      WindowEvent::CloseRequested => event_loop.exit(),
      WindowEvent::Resized(size) => {
        self.resize(size.width, size.height);
        if let Some(window) = self.window.as_ref() {
          window.request_redraw();
        }
      }
      WindowEvent::RedrawRequested => {
        if let Err(error) = self.redraw() {
          self.error = Some(error);
          event_loop.exit();
        }
      }
      // Окно снова видно — кадры идут дальше. Игра сама себя ограничивает, как и в браузере.
      WindowEvent::Occluded(false) | WindowEvent::Focused(true) => {
        if let Some(window) = self.window.as_ref() {
          window.request_redraw();
        }
      }
      _ => {}
    }
  }

  fn about_to_wait(&mut self, _event_loop: &ActiveEventLoop) {
    if let Some(window) = self.window.as_ref() {
      window.request_redraw();
    }
  }
}

/// Выбирает конфигурацию для окна: сглаживание и глубина не нужны — порядок отрисовки задают проходы игры.
fn pick_config(configs: Box<dyn Iterator<Item = Config> + '_>) -> Config {
  configs.reduce(|best, next| if next.num_samples() > best.num_samples() { next } else { best }).unwrap()
}
