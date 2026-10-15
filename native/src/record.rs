//! Запись геймплея: ради неё оболочка и затевалась. Браузерная запись идёт в реальном времени — с пропущенными кадрами
//! и размером окна, — а здесь время ведёт сама оболочка: кадр номер N приходит игре как N / fps секунд, сколько бы он
//! ни считался на деле. Поэтому ролик получается кадр-в-кадр, любого размера и обычно быстрее реального времени.
//!
//! Рисует игра не в окно, а в свой буфер кадра размера ролика: «экран» WebGL (`bindFramebuffer(null)`) привязки
//! подменяют на него (см. `gl::screen`). Окно показывает уменьшенную копию — по ней видно, что пишется. Кадр читается
//! с видеокарты и уходит в ffmpeg по трубе; звука у оболочки пока нет (пункт 10.3 плана).
//!
//! Включается переменными окружения (см. README, «Запись геймплея»): `KHAROS_RECORD=ролик.mp4` и необязательные
//! `KHAROS_SIZE=1080x1920`, `KHAROS_FPS=60`, `KHAROS_SECONDS=20`, `KHAROS_SKIP=2`, `KHAROS_RATIO=2`.

use std::cell::Cell;
use std::io::Write;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::time::Instant;

use anyhow::{anyhow, Context, Result};
use deno_core::op2;
use glow::HasContext;

/// Что и как записывать.
pub struct Options {
  /// Куда лечь ролику: формат ffmpeg берёт по расширению.
  pub path: String,
  /// Размер ролика в пикселях: по умолчанию — вертикальный, под короткие ролики.
  pub width: u32,
  pub height: u32,
  pub fps: u32,
  /// Длина ролика в секундах — без пропущенного начала.
  pub seconds: f64,
  /// Сколько секунд игры пропустить, прежде чем писать: сценка успевает разойтись, а камера — встать.
  pub skip: f64,
  /// Плотность пикселей: во сколько раз пиксель игры крупнее пикселя ролика. Как `devicePixelRatio` у страницы —
  /// при 2 ролик 1080×1920 показывает столько же мира, сколько окно 540×960.
  pub ratio: f64,
}

/// Число из переменной окружения; нет её — значение по умолчанию.
fn number<T: std::str::FromStr>(name: &str, fallback: T) -> Result<T> {
  match std::env::var(name) {
    Ok(value) => value.trim().parse().map_err(|_| anyhow!("{name}={value}: ждём число")),
    Err(_) => Ok(fallback),
  }
}

impl Options {
  /// Настройки записи из окружения; без `KHAROS_RECORD` записи нет, и окно идёт как обычно.
  pub fn from_env() -> Result<Option<Self>> {
    let Ok(path) = std::env::var("KHAROS_RECORD") else {
      return Ok(None);
    };
    let size = std::env::var("KHAROS_SIZE").unwrap_or_else(|_| "1080x1920".into());
    let (width, height) = size
      .split_once('x')
      .and_then(|(width, height)| Some((width.parse().ok()?, height.parse().ok()?)))
      .ok_or_else(|| anyhow!("KHAROS_SIZE={size}: ждём ширину и высоту, например 1080x1920"))?;
    // Кодировщик берёт кадры только чётных сторон: у цвета в yuv420p половинное разрешение.
    if width % 2 == 1 || height % 2 == 1 || width == 0 || height == 0 {
      return Err(anyhow!("KHAROS_SIZE={size}: стороны должны быть чётными и не нулевыми"));
    }
    let options = Self {
      path,
      width,
      height,
      fps: number("KHAROS_FPS", 60)?.max(1),
      seconds: number("KHAROS_SECONDS", 20.0)?,
      skip: number("KHAROS_SKIP", 2.0)?,
      ratio: number("KHAROS_RATIO", 2.0)?,
    };
    Ok(Some(options))
  }
}

thread_local! {
  /// Пошла ли игра: до этого в кадре экран загрузки или пустота, и писать их незачем. Ставит игра (`__kharos_roll`).
  static ROLLING: Cell<bool> = const { Cell::new(false) };
}

/// Игра встала и рисует мир: запись может начинаться. Без записи вызов ничего не значит.
#[op2(fast)]
pub fn op_record_roll() {
  ROLLING.with(|rolling| rolling.set(true));
}

deno_core::extension!(kharos_record, ops = [op_record_roll]);

pub struct Recorder {
  options: Options,
  /// Буфер кадра ролика и его цвет: игра рисует сюда, думая, что рисует на экран.
  framebuffer: glow::Framebuffer,
  _color: glow::Renderbuffer,
  ffmpeg: Child,
  stdin: Option<ChildStdin>,
  pixels: Vec<u8>,
  /// Сколько кадров отдано игре — по нему считается время игры.
  frames: u64,
  /// Сколько кадров пропущено и записано после того, как игра пошла.
  skipped: u64,
  written: u64,
  started: Instant,
}

impl Recorder {
  /// Заводит буфер кадра ролика, подменяет им экран игры и запускает кодировщик.
  pub fn new(options: Options) -> Result<Self> {
    let (width, height) = (options.width as i32, options.height as i32);
    let (framebuffer, color) = crate::gl::with(|gl| unsafe {
      let color = gl.create_renderbuffer().map_err(|error| anyhow!("буфер цвета не создался: {error}"))?;
      gl.bind_renderbuffer(glow::RENDERBUFFER, Some(color));
      gl.renderbuffer_storage(glow::RENDERBUFFER, glow::RGBA8, width, height);
      let framebuffer = gl.create_framebuffer().map_err(|error| anyhow!("буфер кадра не создался: {error}"))?;
      gl.bind_framebuffer(glow::FRAMEBUFFER, Some(framebuffer));
      gl.framebuffer_renderbuffer(glow::FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::RENDERBUFFER, Some(color));
      if gl.check_framebuffer_status(glow::FRAMEBUFFER) != glow::FRAMEBUFFER_COMPLETE {
        return Err(anyhow!("буфер кадра {width}×{height} драйвер не принял"));
      }
      Ok((framebuffer, color))
    })
    .ok_or_else(|| anyhow!("нет контекста OpenGL"))??;
    crate::gl::screen(Some(framebuffer));

    let size = format!("{}x{}", options.width, options.height);
    // Кадры идут снизу вверх (так их отдаёт OpenGL) — переворачивает их ffmpeg, это дешевле, чем здесь.
    let mut ffmpeg = Command::new("ffmpeg")
      .args(["-y", "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", &size])
      .args(["-r", &options.fps.to_string(), "-i", "-", "-vf", "vflip"])
      .args(["-c:v", "libx264", "-preset", "medium", "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart"])
      .arg(&options.path)
      .stdin(Stdio::piped())
      .spawn()
      .context("ffmpeg не запустился — он должен быть в PATH")?;
    let stdin = ffmpeg.stdin.take();
    let pixels = vec![0u8; (options.width * options.height * 4) as usize];
    println!("запись: {} — {size}, {} кадров/с, {} с после {} с разгона", options.path, options.fps, options.seconds, options.skip);
    Ok(Self { options, framebuffer, _color: color, ffmpeg, stdin, pixels, frames: 0, skipped: 0, written: 0, started: Instant::now() })
  }

  /// Размер кадра игры и его плотность: игра узнаёт их вместо размера окна.
  pub fn size(&self) -> (f64, f64, f64) {
    (self.options.width as f64, self.options.height as f64, self.options.ratio)
  }

  /// Время игры для следующего кадра, в миллисекундах: ровный шаг, а не часы.
  pub fn next_clock(&mut self) -> f64 {
    let clock = self.frames as f64 * 1000.0 / self.options.fps as f64;
    self.frames += 1;
    clock
  }

  /// Забирает нарисованный кадр. Возвращает true, когда ролик набран целиком.
  pub fn capture(&mut self) -> Result<bool> {
    if !ROLLING.with(Cell::get) {
      return Ok(false);
    }
    let fps = self.options.fps as f64;
    if (self.skipped as f64) < self.options.skip * fps {
      self.skipped += 1;
      return Ok(false);
    }
    let (width, height) = (self.options.width as i32, self.options.height as i32);
    let pixels = &mut self.pixels;
    crate::gl::with(|gl| unsafe {
      let read = gl.get_parameter_framebuffer(glow::READ_FRAMEBUFFER_BINDING);
      let alignment = gl.get_parameter_i32(glow::PACK_ALIGNMENT);
      gl.bind_framebuffer(glow::READ_FRAMEBUFFER, Some(self.framebuffer));
      gl.pixel_store_i32(glow::PACK_ALIGNMENT, 1);
      gl.read_pixels(0, 0, width, height, glow::RGBA, glow::UNSIGNED_BYTE, glow::PixelPackData::Slice(Some(pixels)));
      gl.pixel_store_i32(glow::PACK_ALIGNMENT, alignment);
      gl.bind_framebuffer(glow::READ_FRAMEBUFFER, read);
    });
    let stdin = self.stdin.as_mut().ok_or_else(|| anyhow!("кодировщик уже закрыт"))?;
    stdin.write_all(&self.pixels).context("ffmpeg перестал принимать кадры")?;
    self.written += 1;
    let total = (self.options.seconds * fps).round() as u64;
    if self.written % self.options.fps as u64 == 0 || self.written == total {
      let pace = self.written as f64 / self.started.elapsed().as_secs_f64();
      println!("запись: {:.0} из {} с ({:.0} кадров/с)", self.written as f64 / fps, self.options.seconds, pace);
    }
    Ok(self.written >= total)
  }

  /// Уменьшенная копия кадра в окне: по ней видно, что пишется. Рисуется прямо в настоящий экран окна, мимо подмены.
  pub fn preview(&self, width: u32, height: u32) {
    let (source_width, source_height) = (self.options.width as i32, self.options.height as i32);
    // Вписываем кадр в окно с сохранением пропорций — по центру.
    let scale = (width as f64 / source_width as f64).min(height as f64 / source_height as f64);
    let (fit_width, fit_height) = ((source_width as f64 * scale) as i32, (source_height as f64 * scale) as i32);
    let (x, y) = ((width as i32 - fit_width) / 2, (height as i32 - fit_height) / 2);
    crate::gl::with(|gl| unsafe {
      // Состояние драйвера после показа должно остаться тем, что оставила игра: она может выставлять его не каждый кадр.
      let scissor = gl.is_enabled(glow::SCISSOR_TEST);
      let (read, draw) = (gl.get_parameter_framebuffer(glow::READ_FRAMEBUFFER_BINDING), gl.get_parameter_framebuffer(glow::DRAW_FRAMEBUFFER_BINDING));
      let mut clear = [0f32; 4];
      gl.get_parameter_f32_slice(glow::COLOR_CLEAR_VALUE, &mut clear);
      gl.bind_framebuffer(glow::READ_FRAMEBUFFER, Some(self.framebuffer));
      gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, None);
      gl.disable(glow::SCISSOR_TEST);
      gl.clear_color(0.0, 0.0, 0.0, 1.0);
      gl.clear(glow::COLOR_BUFFER_BIT);
      gl.blit_framebuffer(0, 0, source_width, source_height, x, y, x + fit_width, y + fit_height, glow::COLOR_BUFFER_BIT, glow::LINEAR);
      gl.clear_color(clear[0], clear[1], clear[2], clear[3]);
      if scissor {
        gl.enable(glow::SCISSOR_TEST);
      }
      gl.bind_framebuffer(glow::READ_FRAMEBUFFER, read);
      gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, draw);
    });
  }

  /// Закрывает трубу и ждёт, пока ffmpeg допишет файл.
  pub fn finish(mut self) -> Result<()> {
    drop(self.stdin.take());
    let status = self.ffmpeg.wait()?;
    if !status.success() {
      return Err(anyhow!("ffmpeg закончил с ошибкой: {status}"));
    }
    let seconds = self.started.elapsed().as_secs_f64();
    println!("запись готова: {} — {} кадров за {seconds:.1} с", self.options.path, self.written);
    Ok(())
  }
}
