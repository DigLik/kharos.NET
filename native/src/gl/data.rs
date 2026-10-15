//! Операции, которым нужны строки, массивы или ответ драйвера: шейдеры, заливка буферов и текстур, юниформы.
//!
//! Массивы приходят сюда прямо из памяти V8 (`#[buffer]`) — копирования нет ни в JS, ни в Rust. Это заметно:
//! атласы по несколько мегабайт заливаются каждый запуск игры, а наборы квадратов — каждый кадр.

use deno_core::op2;
use glow::HasContext;

use super::{desktop, find, put, with, Res};

/// Имя программы, обычная: `#version 300 es` → `#version 330 core`, строки `precision` убираются.
/// Игра пишет шейдеры для браузера (GLSL ES), а настольный драйвер такой версии не знает: её надо переписать.
fn rewrite(source: &str) -> String {
  let mut out = String::with_capacity(source.len());
  for (index, line) in source.lines().enumerate() {
    let trimmed = line.trim_start();
    if trimmed.starts_with("precision ") {
      continue;
    }
    if index == 0 && trimmed.starts_with("#version 300 es") {
      out.push_str("#version 330 core\n");
      continue;
    }
    out.push_str(line);
    out.push('\n');
  }
  out
}

/// Отдаёт драйверу исходник шейдера, переведя версию GLSL, если контекст настольный.
#[op2(fast)]
pub fn op_gl_shader_source(shader: u32, #[string] source: String) {
  let source = if desktop() { rewrite(&source) } else { source };
  if let Some(shader) = find!(shader, Shader) {
    with(|gl| unsafe { gl.shader_source(shader, &source) });
  }
}

/// Параметр шейдера: удалась ли сборка (`COMPILE_STATUS`). Остальное драйвером не спрашивается.
#[op2(fast)]
pub fn op_gl_get_shader_parameter(shader: u32, pname: u32) -> f64 {
  const COMPILE_STATUS: u32 = 0x8B81;
  if pname != COMPILE_STATUS {
    return 0.0;
  }
  find!(shader, Shader)
    .and_then(|shader| with(|gl| unsafe { gl.get_shader_compile_status(shader) }))
    .map(|ok| if ok { 1.0 } else { 0.0 })
    .unwrap_or(0.0)
}

/// Журнал сборки шейдера: текст ошибки, если сборка не удалась.
#[op2]
#[string]
pub fn op_gl_get_shader_info_log(shader: u32) -> String {
  find!(shader, Shader)
    .and_then(|shader| with(|gl| unsafe { gl.get_shader_info_log(shader) }))
    .unwrap_or_default()
}

/// Параметр программы: удалась ли сборка (`LINK_STATUS`), сколько в ней юниформов и атрибутов.
#[op2(fast)]
pub fn op_gl_get_program_parameter(program: u32, pname: u32) -> f64 {
  const LINK_STATUS: u32 = 0x8B82;
  const ACTIVE_UNIFORMS: u32 = 0x8B86;
  const ACTIVE_ATTRIBUTES: u32 = 0x8B89;
  let Some(program) = find!(program, Program) else {
    return 0.0;
  };
  match pname {
    LINK_STATUS => with(|gl| unsafe { gl.get_program_link_status(program) })
      .map(|ok| if ok { 1.0 } else { 0.0 })
      .unwrap_or(0.0),
    ACTIVE_UNIFORMS => with(|gl| unsafe { gl.get_active_uniforms(program) }).unwrap_or(0) as f64,
    ACTIVE_ATTRIBUTES => with(|gl| unsafe { gl.get_active_attributes(program) }).unwrap_or(0) as f64,
    _ => 0.0,
  }
}

/// Журнал сборки программы: текст ошибки, если программа не собралась.
#[op2]
#[string]
pub fn op_gl_get_program_info_log(program: u32) -> String {
  find!(program, Program)
    .and_then(|program| with(|gl| unsafe { gl.get_program_info_log(program) }))
    .unwrap_or_default()
}

/// Юниформ по имени: номер для драйвера или 0, если такого юниформа в шейдере нет (в WebGL — null).
#[op2(fast)]
pub fn op_gl_get_uniform_location(program: u32, #[string] name: &str) -> f64 {
  let Some(program) = find!(program, Program) else {
    return 0.0;
  };
  match with(|gl| unsafe { gl.get_uniform_location(program, name) }) {
    Some(Some(location)) => put(|| Res::Uniform(location)),
    _ => 0.0,
  }
}

/// Атрибут по имени: его место или -1, если шейдер его не использует (в WebGL — -1).
#[op2(fast)]
pub fn op_gl_get_attrib_location(program: u32, #[string] name: &str) -> f64 {
  let Some(program) = find!(program, Program) else {
    return -1.0;
  };
  with(|gl| unsafe { gl.get_attrib_location(program, name) })
    .flatten()
    .map(|location| location as f64)
    .unwrap_or(-1.0)
}

/// Закрепляет за атрибутом место: игра даёт нулевое место общему углу квадрата (`aCorner`).
#[op2(fast)]
pub fn op_gl_bind_attrib_location(program: u32, index: u32, #[string] name: String) {
  if let Some(program) = find!(program, Program) {
    with(|gl| unsafe { gl.bind_attrib_location(program, index, &name) });
  }
}

/// Юниформ программы по номеру: имя, размер и вид. Игра обходит их, чтобы заранее развести установщики.
#[derive(serde::Serialize)]
pub struct ActiveUniform {
  pub name: String,
  pub size: i32,
  #[serde(rename = "type")]
  pub kind: u32,
}

#[op2]
#[serde]
pub fn op_gl_get_active_uniform(program: u32, index: u32) -> ActiveUniform {
  let empty = ActiveUniform { name: String::new(), size: 0, kind: 0 };
  let Some(program) = find!(program, Program) else {
    return empty;
  };
  match with(|gl| unsafe { gl.get_active_uniform(program, index) }) {
    Some(Some(info)) => ActiveUniform { name: info.name, size: info.size, kind: info.utype },
    _ => empty,
  }
}

/// Заливает буфер целиком. Массив приходит из памяти V8 как есть.
#[op2(fast)]
pub fn op_gl_buffer_data(target: u32, buffer: u32, #[buffer] data: &[u8], usage: u32) {
  if let Some(buffer) = find!(buffer, Buffer) {
    with(|gl| unsafe {
      gl.bind_buffer(target, Some(buffer));
      gl.buffer_data_u8_slice(target, data, usage);
    });
  }
}

/// Дозаписывает буфер с байтового смещения: так набор квадратов обновляется каждый кадр без пересоздания памяти.
/// Драйвер берёт буфер из связывания, поэтому связываем его сами — в игре буфер мог быть не тот.
#[op2(fast)]
pub fn op_gl_buffer_sub_data(target: u32, buffer: u32, offset: u32, #[buffer] data: &[u8]) {
  if let Some(buffer) = find!(buffer, Buffer) {
    with(|gl| unsafe {
      gl.bind_buffer(target, Some(buffer));
      gl.buffer_sub_data_u8_slice(target, offset as i32, data);
    });
  }
}

/// Заводит и заливает плоскость текстуры. Пустой массив — «памяти пока нет»: в WebGL это null вместо пикселей.
#[op2(fast)]
pub fn op_gl_tex_image_2d(
  target: u32,
  level: i32,
  internal: i32,
  width: i32,
  height: i32,
  border: i32,
  format: u32,
  kind: u32,
  #[buffer] data: &[u8],
) {
  // Драйвер берёт пиксели из памяти или из буфера: пустой массив — «памяти пока нет», как `null` в WebGL.
  let pixels = if data.is_empty() {
    glow::PixelUnpackData::Slice(None)
  } else {
    glow::PixelUnpackData::Slice(Some(data))
  };
  with(|gl| unsafe { gl.tex_image_2d(target, level, internal, width, height, border, format, kind, pixels) });
}

/// Текстура из двумерного холста — `texImage2D(target, level, internal, format, type, canvas)`: так в кадр ложится
/// холст интерфейса (пункт 10.4 плана). Пиксели берутся прямо у холста, без копии через JS. premultiply и flip — флаги
/// `UNPACK_PREMULTIPLY_ALPHA_WEBGL` и `UNPACK_FLIP_Y_WEBGL`, как их выставила игра.
#[op2(fast)]
#[allow(clippy::too_many_arguments)]
pub fn op_gl_tex_image_canvas(target: u32, level: i32, internal: i32, format: u32, kind: u32, canvas: u32, premultiply: bool, flip: bool) {
  crate::canvas::with_pixels(canvas, premultiply, |width, height, pixels| {
    let flipped;
    let pixels = if flip {
      let row = width as usize * 4;
      flipped = pixels.chunks_exact(row.max(1)).rev().flatten().copied().collect::<Vec<u8>>();
      &flipped[..]
    } else {
      pixels
    };
    let data = if pixels.is_empty() { None } else { Some(pixels) };
    with(|gl| unsafe {
      gl.pixel_store_i32(glow::UNPACK_ALIGNMENT, 1);
      gl.tex_image_2d(target, level, internal, width as i32, height as i32, 0, format, kind, glow::PixelUnpackData::Slice(data));
    });
  });
}

/// Записывает пиксели в прямоугольник текстуры: так атласы заливаются полосами при запуске игры.
#[op2(fast)]
pub fn op_gl_tex_sub_image_2d(
  target: u32,
  level: i32,
  x: i32,
  y: i32,
  width: i32,
  height: i32,
  format: u32,
  kind: u32,
  #[buffer] data: &[u8],
) {
  let pixels = if data.is_empty() {
    glow::PixelUnpackData::Slice(None)
  } else {
    glow::PixelUnpackData::Slice(Some(data))
  };
  with(|gl| unsafe {
    gl.tex_sub_image_2d(target, level, x, y, width, height, format, kind, pixels);
  });
}

/// Юниформ-массив: числа приходят байтами (`Float32Array` как есть) и раскладываются драйверу.
/// Такие юниформы игра задаёт каждый кадр — вид кадра и масштаб (`Frame.view`), поэтому и без копирования.
#[op2(fast)]
pub fn op_gl_uniform_fv(location: u32, size: u32, #[buffer] data: &[u8]) {
  let values: Vec<f32> = data.chunks_exact(4).map(|chunk| f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]])).collect();
  let location = find!(location, Uniform);
  with(|gl| unsafe {
    match size {
      1 => gl.uniform_1_f32_slice(location.as_ref(), &values),
      2 => gl.uniform_2_f32_slice(location.as_ref(), &values),
      3 => gl.uniform_3_f32_slice(location.as_ref(), &values),
      4 => gl.uniform_4_f32_slice(location.as_ref(), &values),
      _ => {}
    }
  });
}

/// Читает пиксели с текущей цели. Нужно для записи геймплея и для проверок под тестами.
#[op2]
#[buffer]
pub fn op_gl_read_pixels(x: i32, y: i32, width: i32, height: i32, format: u32, kind: u32) -> Vec<u8> {
  // Плотность строки — четыре байта на пиксель (RGBA): того требует и WebGL по умолчанию.
  let mut pixels = vec![0u8; (width.max(0) as usize) * (height.max(0) as usize) * 4];
  with(|gl| unsafe { gl.read_pixels(x, y, width, height, format, kind, glow::PixelPackData::Slice(Some(&mut pixels))) });
  pixels
}

/// Печать из игры: игра зовёт `console.log`, он приходит сюда.
#[op2(fast)]
pub fn op_gl_log(#[string] message: String) {
  println!("{message}");
}

