//! Нативная оболочка Kharos: окно, настоящий OpenGL и рантайм JavaScript вместо браузера.
//!
//! Игра не переписывается: её код на TypeScript идёт в рантайм как есть, а браузерный API заменяют три части —
//! обёртка `js/prelude.js` (холст, кадры, таймеры, состояние WebGL), привязки `src/gl` (настоящие вызовы драйвера)
//! и `src/web` (то, что в браузере даёт движок, а не страница). Собранная игра лежит рядом с привязками
//! (`js/game.bundle.js`) — её кладёт сборщик, см. README.

mod audio;
mod canvas;
mod engine;
mod gl;
mod icon;
mod modules;
mod pace;
mod profile;
mod record;
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
use winit::event::{ElementState, MouseButton, MouseScrollDelta, WindowEvent};
use winit::keyboard::{Key, PhysicalKey};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
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

/// Что окно говорит игре до её начала — объект JS, его читают обёртка и вход игры (`__kharos_options`):
/// - `fixed` — время ведёт окно ровным шагом (запись), а не часы;
/// - `scene` — какую сценку показывать (`KHAROS_SCENE`, см. `SCENES` в `src/sim/showcase.ts`);
/// - `hour` — с какого часа суток начать (`KHAROS_HOUR`): свет в ролике не должен зависеть от случая;
/// - `mode` — настоящая игра вместо сценки (`KHAROS_MODE`: `play`, `sandbox`, `battle`), с мышью и клавишами;
/// - `lang` — язык строк из `LANG`.
fn game_options(fixed: bool) -> Result<String> {
  let scene = match std::env::var("KHAROS_SCENE") {
    Ok(name) if name.chars().all(|c| c.is_ascii_alphanumeric()) => format!("'{name}'"),
    Ok(name) => return Err(anyhow!("KHAROS_SCENE={name}: имя сценки — латинские буквы")),
    Err(_) => "null".into(),
  };
  let hour = match std::env::var("KHAROS_HOUR") {
    Ok(value) => value.trim().parse::<f64>().map_err(|_| anyhow!("KHAROS_HOUR={value}: ждём число"))?.to_string(),
    Err(_) => "null".into(),
  };
  let mode = match std::env::var("KHAROS_MODE") {
    Ok(name) if ["play", "sandbox", "battle"].contains(&name.as_str()) => format!("'{name}'"),
    Ok(name) => return Err(anyhow!("KHAROS_MODE={name}: ждём play, sandbox или battle")),
    Err(_) => "null".into(),
  };
  // Язык строк — из окружения, как его берут прочие программы: `ru_RU.UTF-8` даёт `ru-RU`.
  let lang = std::env::var("LANG")
    .ok()
    .and_then(|value| value.split('.').next().map(|tag| tag.replace('_', "-")))
    .filter(|tag| !tag.is_empty() && tag != "C" && tag.chars().all(|c| c.is_ascii_alphanumeric() || c == '-'))
    .map_or("null".into(), |tag| format!("'{tag}'"));
  Ok(format!("{{ fixed: {fixed}, scene: {scene}, hour: {hour}, mode: {mode}, lang: {lang} }}"))
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
  /// Запись ролика: с ней время ведёт окно, а кадры уходят в кодировщик (см. `record.rs`).
  recorder: Option<record::Recorder>,
  /// Ошибка, случившаяся уже во время работы: кадры после неё не идут, а сборка сообщает о ней на выходе.
  error: Option<anyhow::Error>,
  /// Замер кадров (`KHAROS_PROFILE`, см. `profile.rs`).
  profile: Option<profile::Profile>,
  /// Часы кадров по смене экрана (см. `pace.rs`); до окна их нет.
  pace: Option<pace::Pace>,
}

impl App {
  /// Создаёт окно, контекст драйвера и рантайм с игрой.
  fn start(&mut self, event_loop: &ActiveEventLoop) -> Result<()> {
    let recording = record::Options::from_env()?;
    // При записи окно — лишь уменьшенный показ ролика: той же формы, что и он, высотой не больше 720.
    let (width, height) = match &recording {
      Some(options) => {
        let scale = (720.0 / options.height as f64).min(1280.0 / options.width as f64).min(1.0);
        (options.width as f64 * scale, options.height as f64 * scale)
      }
      None => (1280.0, 720.0),
    };
    let attributes = WindowAttributes::default()
      .with_title(if recording.is_some() { "Kharos — запись" } else { "Kharos" })
      .with_inner_size(LogicalSize::new(width, height))
      .with_window_icon(icon::window_icon());
    let template = ConfigTemplateBuilder::new().with_alpha_size(8);
    // Берём конфигурацию с наибольшим числом сэмплов: картинку рисуют проходы, и лишнее сглаживание только мешает.
    let (window, config) = DisplayBuilder::new()
      .with_window_attributes(Some(attributes))
      .build(event_loop, template, pick_config)
      .map_err(|error| anyhow!("окно не создалось: {error}"))?;
    let window = window.ok_or_else(|| anyhow!("окно не создалось"))?;
    icon::dock_icon();
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
    // Показ ждёт вертикальную синхронизацию: игра считает кадры от времени, как и в браузере. При записи ждать
    // нечего — кадры считаются так быстро, как получится.
    let interval = if recording.is_some() { SwapInterval::DontWait } else { SwapInterval::Wait(NonZeroU32::new(1).unwrap()) };
    let _ = surface.set_swap_interval(&context, interval);

    gl::install(driver, desktop);
    // Буфер записи заводится до игры: так «экраном» он служит с самого первого её вызова.
    let fixed = recording.is_some();
    self.recorder = recording.map(record::Recorder::new).transpose()?;
    let mut engine = engine::Engine::new()?;
    engine.run(&game_url()?, &game_options(fixed)?)?;
    let (width, height, ratio) = match &self.recorder {
      Some(recorder) => recorder.size(),
      None => {
        let size = window.inner_size();
        (size.width as f64, size.height as f64, window.scale_factor())
      }
    };
    engine.resize(width, height, ratio)?;

    self.window = Some(window);
    self.surface = Some(surface);
    self.context = Some(context);
    self.engine = Some(engine);
    let hertz = self.window.as_ref().and_then(|window| window.current_monitor()).and_then(|monitor| monitor.refresh_rate_millihertz());
    let hertz = hertz.map_or(0.0, |millihertz| millihertz as f64 / 1000.0);
    self.profile = profile::Profile::from_env(hertz);
    self.pace = Some(pace::Pace::new(hertz));
    Ok(())
  }

  /// Один кадр: игра считает свой кадр, Rust показывает нарисованное. При записи кадр ещё и уходит в ролик;
  /// true — ролик набран целиком.
  fn redraw(&mut self) -> Result<bool> {
    let (Some(engine), Some(surface), Some(context), Some(pace)) =
      (self.engine.as_mut(), self.surface.as_ref(), self.context.as_ref(), self.pace.as_mut())
    else {
      return Ok(false);
    };
    let Some(recorder) = self.recorder.as_mut() else {
      let begun = Instant::now();
      let clock = pace.next(web::now_ms());
      engine.frame(clock)?;
      let drawn = Instant::now();
      surface.swap_buffers(context)?;
      if let Some(profile) = self.profile.as_mut() {
        profile.frame(begun, drawn, Instant::now(), clock);
      }
      return Ok(false);
    };
    engine.frame(recorder.next_clock())?;
    let done = recorder.capture()?;
    if let Some(window) = self.window.as_ref() {
      let size = window.inner_size();
      recorder.preview(size.width, size.height);
    }
    surface.swap_buffers(context)?;
    Ok(done)
  }

  /// Ввод от окна уходит в игру (см. `__kharos_input` в `js/prelude.js`): там из него собираются события страницы.
  /// Координаты — физические пиксели окна, в пиксели страницы их переводит обёртка. При записи ввода нет: кадр ролика
  /// другого размера, чем окно, и указатель в нём попадал бы не туда.
  fn input(&mut self, event: &WindowEvent) {
    if self.recorder.is_some() {
      return;
    }
    let ratio = self.window.as_ref().map_or(1.0, |window| window.scale_factor());
    let arguments = match event {
      WindowEvent::CursorMoved { position, .. } => format!("'move', {}, {}", position.x, position.y),
      WindowEvent::CursorLeft { .. } => "'leave'".into(),
      WindowEvent::MouseInput { state, button, .. } => {
        // Номера кнопок — как у страницы: левая 0, средняя 1, правая 2.
        let button = match button {
          MouseButton::Left => 0,
          MouseButton::Middle => 1,
          MouseButton::Right => 2,
          MouseButton::Back => 3,
          MouseButton::Forward => 4,
          MouseButton::Other(_) => return,
        };
        let kind = if *state == ElementState::Pressed { "down" } else { "up" };
        format!("'{kind}', {button}")
      }
      // Колесо — в пикселях страницы и со знаком страницы: от себя — отрицательно. Щелчок колеса браузер считает за
      // сто пикселей.
      WindowEvent::MouseWheel { delta, .. } => match delta {
        MouseScrollDelta::LineDelta(x, y) => format!("'wheel', {}, {}", -x * 100.0, -y * 100.0),
        MouseScrollDelta::PixelDelta(position) => format!("'wheel', {}, {}", -position.x / ratio, -position.y / ratio),
      },
      WindowEvent::KeyboardInput { event, .. } => {
        // Имена клавиш winit берёт из той же спецификации, что и страница (`KeyA`, `Digit1`, `ArrowUp`, `Escape`).
        let PhysicalKey::Code(code) = event.physical_key else {
          return;
        };
        let key = match &event.logical_key {
          Key::Character(text) => text.to_string(),
          Key::Named(name) => format!("{name:?}"),
          _ => String::new(),
        };
        let down = event.state == ElementState::Pressed;
        format!("'key', {down}, {}, {}, {}", js_string(&format!("{code:?}")), js_string(&key), event.repeat)
      }
      WindowEvent::ModifiersChanged(modifiers) => {
        let state = modifiers.state();
        format!("'modifiers', {}, {}, {}, {}", state.shift_key(), state.control_key(), state.alt_key(), state.super_key())
      }
      WindowEvent::Focused(false) => "'blur'".into(),
      _ => return,
    };
    if let Some(engine) = self.engine.as_mut() {
      if let Err(error) = engine.input(&arguments) {
        eprintln!("ввод не дошёл до игры: {error}");
      }
    }
  }

  /// Кадр и то, что за ним следует: ошибка или готовый ролик закрывают окно.
  fn step(&mut self, event_loop: &ActiveEventLoop) {
    match self.redraw() {
      Ok(false) => {}
      Ok(true) => {
        if let Some(recorder) = self.recorder.take() {
          if let Err(error) = recorder.finish() {
            self.error = Some(error);
          }
        }
        event_loop.exit();
      }
      Err(error) => {
        self.error = Some(error);
        event_loop.exit();
      }
    }
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
      // При записи размер кадра — размер ролика, и окно его не меняет.
      if self.recorder.is_some() {
        return;
      }
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
    // При записи кадры идут подряд, без запроса окна: циклу событий нельзя засыпать до следующего события.
    if self.recorder.is_some() {
      event_loop.set_control_flow(ControlFlow::Poll);
    }
    if let Some(window) = self.window.as_ref() {
      window.request_redraw();
    }
  }

  fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
    self.input(&event);
    match event {
      WindowEvent::CloseRequested => event_loop.exit(),
      WindowEvent::Resized(size) => {
        self.resize(size.width, size.height);
        if let Some(window) = self.window.as_ref() {
          window.request_redraw();
        }
      }
      // При записи кадры идут не по запросу окна (см. about_to_wait): закрытое другим окно их бы не получало.
      WindowEvent::RedrawRequested if self.recorder.is_none() => self.step(event_loop),
      // Окно снова видно — кадры идут дальше. Игра сама себя ограничивает, как и в браузере.
      WindowEvent::Occluded(false) | WindowEvent::Focused(true) => {
        if let Some(window) = self.window.as_ref() {
          window.request_redraw();
        }
      }
      _ => {}
    }
  }

  fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
    if self.recorder.is_some() {
      self.step(event_loop);
    } else if let Some(window) = self.window.as_ref() {
      window.request_redraw();
    }
  }
}

/// Строка для JS: всё, кроме латиницы и цифр, — кодами UTF-16, так что ни кавычка, ни перевод строки её не разорвут.
fn js_string(text: &str) -> String {
  let mut out = String::from("'");
  for unit in text.encode_utf16() {
    match char::from_u32(unit as u32) {
      Some(c) if c.is_ascii_alphanumeric() => out.push(c),
      _ => out.push_str(&format!("\\u{unit:04x}")),
    }
  }
  out.push('\'');
  out
}

/// Выбирает конфигурацию для окна: сглаживание и глубина не нужны — порядок отрисовки задают проходы игры.
fn pick_config(configs: Box<dyn Iterator<Item = Config> + '_>) -> Config {
  configs.reduce(|best, next| if next.num_samples() > best.num_samples() { next } else { best }).unwrap()
}
