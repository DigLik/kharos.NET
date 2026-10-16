//! Общая операция: числа, связи и мелкие вызовы. Всё, что несёт большие массивы, живёт в `data.rs`.
//!
//! Разложить вызовы так пришлось ради нулевого копирования: `#[op2(fast)]` принимает только числа, а массивы
//! приходят отдельными операциями прямо из памяти V8.

use deno_core::op2;
use glow::HasContext;

use super::call;
use super::{find, put, with, Res};

/// Вызов WebGL, которому не нужны массивы: связать буфер, задать юниформу, нарисовать.
/// Обёртка зовёт его так: `op_gl(ops.drawArrays, mode, first, count, 0, 0, 0)`.
/// Номера ресурсов, константы OpenGL и размеры идут числами; возврат — номер нового ресурса или ответ драйвера,
/// а там, где WebGL отвечает `undefined`, — `NaN`.
#[op2(fast)]
pub fn op_gl(opcode: u32, a: f64, b: f64, c: f64, d: f64, e: f64, f: f64) -> f64 {
  let u = |v: f64| v as u32;
  let i = |v: f64| v as i32;
  match opcode {
    call::ENABLE => {
      with(|gl| unsafe { gl.enable(u(a)) });
    }
    call::DISABLE => {
      with(|gl| unsafe { gl.disable(u(a)) });
    }
    call::DISABLE_ATTRIB => {
      with(|gl| unsafe { gl.disable_vertex_attrib_array(u(a)) });
    }
    call::BLEND_FUNC => {
      with(|gl| unsafe { gl.blend_func(u(a), u(b)) });
    }
    call::CLEAR => {
      with(|gl| unsafe { gl.clear(u(a)) });
    }
    call::CLEAR_COLOR => {
      with(|gl| unsafe { gl.clear_color(a as f32, b as f32, c as f32, d as f32) });
    }
    call::VIEWPORT => {
      with(|gl| unsafe { gl.viewport(i(a), i(b), i(c), i(d)) });
    }
    call::BIND_BUFFER => {
      let buffer = if a > 0.0 { find!(a, Buffer) } else { None };
      with(|gl| unsafe { gl.bind_buffer(u(b), buffer) });
    }
    call::BIND_VERTEX_ARRAY => {
      let array = if a > 0.0 { find!(a, VertexArray) } else { None };
      with(|gl| unsafe { gl.bind_vertex_array(array) });
    }
    call::BIND_TEXTURE => {
      let texture = if b > 0.0 { find!(b, Texture) } else { None };
      with(|gl| unsafe { gl.bind_texture(u(a), texture) });
    }
    call::BIND_FRAMEBUFFER => {
      let target = if a > 0.0 { find!(a, Framebuffer) } else { super::SCREEN.with(std::cell::Cell::get) };
      with(|gl| unsafe { gl.bind_framebuffer(u(b), target) });
    }
    call::ACTIVE_TEXTURE => {
      with(|gl| unsafe { gl.active_texture(u(a)) });
    }
    call::USE_PROGRAM => {
      let program = if a > 0.0 { find!(a, Program) } else { None };
      with(|gl| unsafe { gl.use_program(program) });
    }
    call::CREATE_BUFFER => {
      if let Some(Ok(buffer)) = with(|gl| unsafe { gl.create_buffer() }) {
        return put(|| Res::Buffer(buffer));
      }
    }
    call::CREATE_VERTEX_ARRAY => {
      if let Some(Ok(array)) = with(|gl| unsafe { gl.create_vertex_array() }) {
        return put(|| Res::VertexArray(array));
      }
    }
    call::CREATE_TEXTURE => {
      if let Some(Ok(texture)) = with(|gl| unsafe { gl.create_texture() }) {
        return put(|| Res::Texture(texture));
      }
    }
    call::CREATE_PROGRAM => {
      if let Some(Ok(program)) = with(|gl| unsafe { gl.create_program() }) {
        return put(|| Res::Program(program));
      }
    }
    call::CREATE_SHADER => {
      if let Some(Ok(shader)) = with(|gl| unsafe { gl.create_shader(u(a)) }) {
        return put(|| Res::Shader(shader));
      }
    }
    call::CREATE_FRAMEBUFFER => {
      if let Some(Ok(target)) = with(|gl| unsafe { gl.create_framebuffer() }) {
        return put(|| Res::Framebuffer(target));
      }
    }
    call::DELETE_BUFFER => {
      if let Some(buffer) = find!(a, Buffer) {
        with(|gl| unsafe { gl.delete_buffer(buffer) });
      }
    }
    call::DELETE_VERTEX_ARRAY => {
      if let Some(array) = find!(a, VertexArray) {
        with(|gl| unsafe { gl.delete_vertex_array(array) });
      }
    }
    call::DELETE_TEXTURE => {
      if let Some(texture) = find!(a, Texture) {
        with(|gl| unsafe { gl.delete_texture(texture) });
      }
    }
    call::DELETE_PROGRAM => {
      if let Some(program) = find!(a, Program) {
        with(|gl| unsafe { gl.delete_program(program) });
      }
    }
    call::DELETE_SHADER => {
      if let Some(shader) = find!(a, Shader) {
        with(|gl| unsafe { gl.delete_shader(shader) });
      }
    }
    call::DELETE_FRAMEBUFFER => {
      if let Some(target) = find!(a, Framebuffer) {
        with(|gl| unsafe { gl.delete_framebuffer(target) });
      }
    }
    call::ENABLE_ATTRIB => {
      with(|gl| unsafe { gl.enable_vertex_attrib_array(u(a)) });
    }
    call::VERTEX_ATTRIB_POINTER => {
      with(|gl| unsafe { gl.vertex_attrib_pointer_f32(u(a), i(b), u(c), d != 0.0, i(e), i(f)) });
    }
    call::VERTEX_ATTRIB_DIVISOR => {
      with(|gl| unsafe { gl.vertex_attrib_divisor(u(a), u(b)) });
    }
    call::ATTACH_SHADER => {
      if let (Some(program), Some(shader)) = (find!(a, Program), find!(b, Shader)) {
        with(|gl| unsafe { gl.attach_shader(program, shader) });
      }
    }
    call::LINK_PROGRAM => {
      if let Some(program) = find!(a, Program) {
        with(|gl| unsafe { gl.link_program(program) });
      }
    }
    call::COMPILE_SHADER => {
      if let Some(shader) = find!(a, Shader) {
        with(|gl| unsafe { gl.compile_shader(shader) });
      }
    }
    call::TEX_PARAMETERI => {
      with(|gl| unsafe { gl.tex_parameter_i32(u(a), u(b), i(c)) });
    }
    call::TEX_STORAGE_2D => {
      with(|gl| unsafe { gl.tex_storage_2d(u(a), i(b), u(c), i(d), i(e)) });
    }
    call::FRAMEBUFFER_TEXTURE_2D => {
      let texture = if d > 0.0 { find!(d, Texture) } else { None };
      with(|gl| unsafe { gl.framebuffer_texture_2d(u(a), u(b), u(c), texture, i(e)) });
    }
    call::DRAW_ARRAYS => {
      with(|gl| unsafe { gl.draw_arrays(u(a), i(b), i(c)) });
    }
    call::DRAW_ARRAYS_INSTANCED => {
      with(|gl| unsafe { gl.draw_arrays_instanced(u(a), i(b), i(c), i(d)) });
    }
    call::UNIFORM_1F => {
      let location = find!(a, Uniform);
      with(|gl| unsafe { gl.uniform_1_f32(location.as_ref(), b as f32) });
    }
    call::UNIFORM_1I => {
      let location = find!(a, Uniform);
      with(|gl| unsafe { gl.uniform_1_i32(location.as_ref(), i(b)) });
    }
    call::IS_CONTEXT_LOST => return 0.0,
    call::FLUSH => {
      with(|gl| unsafe { gl.flush() });
    }
    call::FINISH => {
      with(|gl| unsafe { gl.finish() });
    }
    call::FENCE_SYNC => {
      // Условие и признаки забора приходят из игры: в WebGL это `SYNC_GPU_COMMANDS_COMPLETE` и ноль.
      if let Some(Ok(fence)) = with(|gl| unsafe { gl.fence_sync(u(a), u(b)) }) {
        return put(|| Res::Fence(fence));
      }
    }
    call::CLIENT_WAIT_SYNC => {
      if let Some(fence) = find!(a, Fence) {
        // WebGL ждёт срок в наносекундах, а драйвер берёт микросекунды в 32 битах; «ждать сколько угодно» — так и остаётся.
        let timeout = if c < 0.0 { i32::MAX } else { (c / 1000.0).min(i32::MAX as f64) as i32 };
        if let Some(status) = with(|gl| unsafe { gl.client_wait_sync(fence, u(b), timeout) }) {
          return status as f64;
        }
      }
    }
    call::DELETE_SYNC => {
      if let Some(fence) = find!(a, Fence) {
        with(|gl| unsafe { gl.delete_sync(fence) });
      }
    }
    call::GENERATE_MIPMAP => {
      with(|gl| unsafe { gl.generate_mipmap(u(a)) });
    }
    call::PIXEL_STOREI => {
      with(|gl| unsafe { gl.pixel_store_i32(u(a), i(b)) });
    }
    _ => {}
  }
  f64::NAN
}
