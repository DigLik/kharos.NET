//! Привязки WebGL 2 к настоящему OpenGL: игра зовёт их как обычно, а Rust ведёт реестр ресурсов и вызывает драйвер.
//!
//! Устройство нарочно тонкое, чтобы игру не переписывать:
//! - номера констант WebGL совпадают с номерами OpenGL, поэтому переводить их не нужно: `glow` принимает
//!   константы как `u32` и получает то же число, что написал бы браузер;
//! - состояние WebGL (что связано, что лежит в атрибутах) держит JS-обёртка `js/prelude.js` — Rust трогают
//!   только тогда, когда состояние действительно надо изменить;
//! - ресурсы (буферы, программы, текстуры) игра видит числами, а Rust держит за ними объекты драйвера;
//! - заливка буферов и текстур — отдельные операции с нулевым копированием: массив приходит прямо из памяти V8.

use std::cell::RefCell;
use std::collections::HashMap;

mod calls;
mod data;

pub use calls::op_gl;
pub use data::{
  op_gl_bind_attrib_location, op_gl_buffer_data, op_gl_buffer_sub_data, op_gl_get_active_uniform,
  op_gl_get_attrib_location, op_gl_get_program_info_log, op_gl_get_program_parameter, op_gl_get_shader_info_log,
  op_gl_get_shader_parameter, op_gl_get_uniform_location, op_gl_log, op_gl_read_pixels, op_gl_shader_source,
  op_gl_tex_image_2d, op_gl_tex_sub_image_2d, op_gl_uniform_fv,
};

/// Ресурс драйвера, заведённый игрой. Игре достаётся его номер, Rust — сам объект.
enum Res {
  Buffer(glow::Buffer),
  VertexArray(glow::VertexArray),
  Texture(glow::Texture),
  Program(glow::Program),
  Shader(glow::Shader),
  Framebuffer(glow::Framebuffer),
  Uniform(glow::UniformLocation),
  Fence(glow::Fence),
}

thread_local! {
  /// Контекст драйвера: один на окно, как и в браузерной игре. Живёт на главном потоке — поэтому его не нужно
  /// делать Send, а хранится он в потоковом месте, чтобы операции не таскали его через состояние рантайма.
  static GL: RefCell<Option<glow::Context>> = const { RefCell::new(None) };
  /// Заведённые ресурсы: отдельно от контекста — тогда поиск ресурса и вызов драйвера не мешают друг другу.
  static RES: RefCell<HashMap<u32, Res>> = RefCell::new(HashMap::new());
}

thread_local! {
  /// Следующий номер ресурса. Номера не переиспользуются: игра, случайно оставившая старый, получит «ничего», а не чужой.
  static NEXT: std::cell::Cell<u32> = const { std::cell::Cell::new(1) };
  /// Настольный ли OpenGL: шейдеры игры написаны на GLSL ES, и на настольном драйвере версию приходится переписывать.
  static DESKTOP: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

/// Принимает загруженный контекст драйвера. Зовётся один раз, при создании окна.
pub fn install(context: glow::Context, desktop: bool) {
  DESKTOP.with(|flag| flag.set(desktop));
  GL.with(|cell| *cell.borrow_mut() = Some(context));
}

/// Настольный ли контекст: нужно провайдерам шейдеров.
pub fn desktop() -> bool {
  DESKTOP.with(std::cell::Cell::get)
}

/// Работа с контекстом драйвера. Наружу контекст не отдаётся: он живёт до конца игры и не переносится между потоками.
fn with<R>(f: impl FnOnce(&glow::Context) -> R) -> Option<R> {
  GL.with(|cell| cell.borrow().as_ref().map(f))
}

/// Кладёт ресурс в реестр и отдаёт его номер игре. Нулевой номер — «ничего», как и в WebGL.
fn put(make: impl FnOnce() -> Res) -> f64 {
  let id = NEXT.with(|next| {
    let id = next.get();
    next.set(id + 1);
    id
  });
  RES.with(|res| res.borrow_mut().insert(id, make()));
  id as f64
}

/// Ищет ресурс по номеру среди нужного вида: у каждого вида драйверные вызовы свои, поэтому чужой номер — не находка.
/// Пути к реестру и его содержимому полные: макрос зовётся из соседних модулей, и своих имён у них нет.
macro_rules! find {
  ($id:expr, $kind:ident) => {
    crate::gl::RES.with(|res| match res.borrow().get(&($id as u32)) {
      Some(crate::gl::Res::$kind(value)) => Some(*value),
      _ => None,
    })
  };
}

pub(crate) use find;


/// Номера вызовов, которые идут общей операцией: их имена видит и JS (см. opcodes_script).
/// Число — единственный источник правды: обёртка берёт имена отсюда, а не повторяет их у себя.
pub mod call {
  pub const ENABLE: u32 = 1;
  pub const DISABLE: u32 = 2;
  pub const BLEND_FUNC: u32 = 3;
  pub const CLEAR: u32 = 4;
  pub const CLEAR_COLOR: u32 = 5;
  pub const VIEWPORT: u32 = 6;
  pub const BIND_BUFFER: u32 = 7;
  pub const BIND_VERTEX_ARRAY: u32 = 8;
  pub const BIND_TEXTURE: u32 = 9;
  pub const BIND_FRAMEBUFFER: u32 = 10;
  pub const ACTIVE_TEXTURE: u32 = 11;
  pub const USE_PROGRAM: u32 = 12;
  pub const CREATE_BUFFER: u32 = 13;
  pub const CREATE_VERTEX_ARRAY: u32 = 14;
  pub const CREATE_TEXTURE: u32 = 15;
  pub const CREATE_PROGRAM: u32 = 16;
  pub const CREATE_SHADER: u32 = 17;
  pub const CREATE_FRAMEBUFFER: u32 = 18;
  pub const DELETE_BUFFER: u32 = 19;
  pub const DELETE_VERTEX_ARRAY: u32 = 20;
  pub const DELETE_TEXTURE: u32 = 21;
  pub const DELETE_PROGRAM: u32 = 22;
  pub const DELETE_SHADER: u32 = 23;
  pub const DELETE_FRAMEBUFFER: u32 = 24;
  pub const ENABLE_ATTRIB: u32 = 25;
  pub const VERTEX_ATTRIB_POINTER: u32 = 26;
  pub const VERTEX_ATTRIB_DIVISOR: u32 = 27;
  pub const ATTACH_SHADER: u32 = 28;
  pub const LINK_PROGRAM: u32 = 29;
  pub const COMPILE_SHADER: u32 = 30;
  pub const TEX_PARAMETERI: u32 = 31;
  pub const TEX_STORAGE_2D: u32 = 32;
  pub const FRAMEBUFFER_TEXTURE_2D: u32 = 33;
  pub const DRAW_ARRAYS: u32 = 34;
  pub const DRAW_ARRAYS_INSTANCED: u32 = 35;
  pub const UNIFORM_1F: u32 = 36;
  pub const UNIFORM_1I: u32 = 37;
  pub const IS_CONTEXT_LOST: u32 = 38;
  pub const FLUSH: u32 = 39;
  pub const FINISH: u32 = 40;
  pub const FENCE_SYNC: u32 = 41;
  pub const CLIENT_WAIT_SYNC: u32 = 42;
  pub const DELETE_SYNC: u32 = 43;
  pub const GENERATE_MIPMAP: u32 = 44;
  pub const PIXEL_STOREI: u32 = 45;
  pub const DISABLE_ATTRIB: u32 = 46;
}

/// Имена вызовов для JS. Порядок и числа берутся отсюда, поэтому обёртка и Rust не могут разъехаться.
const OPCODES: &[(&str, u32)] = &[
  ("enable", call::ENABLE),
  ("disable", call::DISABLE),
  ("blendFunc", call::BLEND_FUNC),
  ("clear", call::CLEAR),
  ("clearColor", call::CLEAR_COLOR),
  ("viewport", call::VIEWPORT),
  ("bindBuffer", call::BIND_BUFFER),
  ("bindVertexArray", call::BIND_VERTEX_ARRAY),
  ("bindTexture", call::BIND_TEXTURE),
  ("bindFramebuffer", call::BIND_FRAMEBUFFER),
  ("activeTexture", call::ACTIVE_TEXTURE),
  ("useProgram", call::USE_PROGRAM),
  ("createBuffer", call::CREATE_BUFFER),
  ("createVertexArray", call::CREATE_VERTEX_ARRAY),
  ("createTexture", call::CREATE_TEXTURE),
  ("createProgram", call::CREATE_PROGRAM),
  ("createShader", call::CREATE_SHADER),
  ("createFramebuffer", call::CREATE_FRAMEBUFFER),
  ("deleteBuffer", call::DELETE_BUFFER),
  ("deleteVertexArray", call::DELETE_VERTEX_ARRAY),
  ("deleteTexture", call::DELETE_TEXTURE),
  ("deleteProgram", call::DELETE_PROGRAM),
  ("deleteShader", call::DELETE_SHADER),
  ("deleteFramebuffer", call::DELETE_FRAMEBUFFER),
  ("enableVertexAttribArray", call::ENABLE_ATTRIB),
  ("disableVertexAttribArray", call::DISABLE_ATTRIB),
  ("vertexAttribPointer", call::VERTEX_ATTRIB_POINTER),
  ("vertexAttribDivisor", call::VERTEX_ATTRIB_DIVISOR),
  ("attachShader", call::ATTACH_SHADER),
  ("linkProgram", call::LINK_PROGRAM),
  ("compileShader", call::COMPILE_SHADER),
  ("texParameteri", call::TEX_PARAMETERI),
  ("texStorage2D", call::TEX_STORAGE_2D),
  ("framebufferTexture2D", call::FRAMEBUFFER_TEXTURE_2D),
  ("drawArrays", call::DRAW_ARRAYS),
  ("drawArraysInstanced", call::DRAW_ARRAYS_INSTANCED),
  ("uniform1f", call::UNIFORM_1F),
  ("uniform1i", call::UNIFORM_1I),
  ("isContextLost", call::IS_CONTEXT_LOST),
  ("flush", call::FLUSH),
  ("finish", call::FINISH),
  ("fenceSync", call::FENCE_SYNC),
  ("clientWaitSync", call::CLIENT_WAIT_SYNC),
  ("deleteSync", call::DELETE_SYNC),
  ("generateMipmap", call::GENERATE_MIPMAP),
  ("pixelStorei", call::PIXEL_STOREI),
];

/// Кладёт имена вызовов в JS. Идёт до обёртки, поэтому числа задаёт только Rust.
pub fn opcodes_script() -> String {
  let pairs: Vec<String> = OPCODES.iter().map(|(name, id)| format!("{name}:{id}")).collect();
  format!("globalThis.__kharos_gl_ops = Object.freeze({{{}}});\n", pairs.join(","))
}

deno_core::extension!(
  kharos_gl,
  ops = [
    op_gl,
    op_gl_shader_source,
    op_gl_get_shader_parameter,
    op_gl_get_shader_info_log,
    op_gl_get_program_parameter,
    op_gl_get_program_info_log,
    op_gl_get_uniform_location,
    op_gl_get_attrib_location,
    op_gl_bind_attrib_location,
    op_gl_get_active_uniform,
    op_gl_buffer_data,
    op_gl_buffer_sub_data,
    op_gl_tex_image_2d,
    op_gl_tex_sub_image_2d,
    op_gl_uniform_fv,
    op_gl_read_pixels,
    op_gl_log,
  ],
  // JS здесь нет нарочно: у кода расширений deno_core требует семибитного ASCII, а обёртка `js/prelude.js`
  // русская. Её кладёт в рантайм `engine.rs` — обычным скриптом.
);

