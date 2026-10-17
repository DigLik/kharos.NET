//! Помощники GL поверх glow: программы, текстуры, цели отрисовки, вершинные массивы с инстансами. Шейдеры пишутся
//! на GLSL ES 3.00 без строки версии: её подставляет `program` — 330 core на десктопе, 300 es на GLES и WebGL 2.

use glow::HasContext as _;

pub struct Gl {
  pub gl: glow::Context,
  pub gles: bool,
}

/// Цель отрисовки: текстура и её кадровый буфер.
pub struct Target {
  pub framebuffer: glow::Framebuffer,
  pub texture: glow::Texture,
  pub size: [i32; 2],
}

impl Gl {
  pub fn new(gl: glow::Context) -> Self {
    let gles = gl.version().is_embedded;
    Self { gl, gles }
  }

  pub fn program(&self, vertex: &str, fragment: &str) -> Result<glow::Program, String> {
    let gl = &self.gl;
    let header = if self.gles { "#version 300 es\nprecision highp float;\nprecision highp int;\n" } else { "#version 330 core\n" };
    unsafe {
      let program = gl.create_program()?;
      let mut shaders = Vec::new();
      for (kind, source) in [(glow::VERTEX_SHADER, vertex), (glow::FRAGMENT_SHADER, fragment)] {
        let shader = gl.create_shader(kind)?;
        gl.shader_source(shader, &format!("{header}{source}"));
        gl.compile_shader(shader);
        if !gl.get_shader_compile_status(shader) {
          return Err(format!("шейдер: {}", gl.get_shader_info_log(shader)));
        }
        gl.attach_shader(program, shader);
        shaders.push(shader);
      }
      gl.link_program(program);
      if !gl.get_program_link_status(program) {
        return Err(format!("программа: {}", gl.get_program_info_log(program)));
      }
      for shader in shaders {
        gl.detach_shader(program, shader);
        gl.delete_shader(shader);
      }
      Ok(program)
    }
  }

  /// Текстура с ближайшим соседом — пиксель-арт не мылится.
  pub fn texture(&self, size: [i32; 2], internal: u32, format: u32, pixels: Option<&[u8]>) -> glow::Texture {
    let gl = &self.gl;
    unsafe {
      let texture = gl.create_texture().expect("текстура");
      gl.bind_texture(glow::TEXTURE_2D, Some(texture));
      gl.pixel_store_i32(glow::UNPACK_ALIGNMENT, 1);
      gl.tex_image_2d(glow::TEXTURE_2D, 0, internal as i32, size[0], size[1], 0, format, glow::UNSIGNED_BYTE, glow::PixelUnpackData::Slice(pixels));
      for (name, value) in [(glow::TEXTURE_MIN_FILTER, glow::NEAREST), (glow::TEXTURE_MAG_FILTER, glow::NEAREST), (glow::TEXTURE_WRAP_S, glow::CLAMP_TO_EDGE), (glow::TEXTURE_WRAP_T, glow::CLAMP_TO_EDGE)] {
        gl.tex_parameter_i32(glow::TEXTURE_2D, name, value as i32);
      }
      texture
    }
  }

  pub fn target(&self, size: [i32; 2], internal: u32, format: u32) -> Target {
    let gl = &self.gl;
    let texture = self.texture(size, internal, format, None);
    let framebuffer = unsafe {
      let framebuffer = gl.create_framebuffer().expect("кадровый буфер");
      gl.bind_framebuffer(glow::FRAMEBUFFER, Some(framebuffer));
      gl.framebuffer_texture_2d(glow::FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(texture), 0);
      gl.bind_framebuffer(glow::FRAMEBUFFER, None);
      framebuffer
    };
    Target { framebuffer, texture, size }
  }

  pub fn drop_target(&self, target: &Target) {
    unsafe {
      self.gl.delete_framebuffer(target.framebuffer);
      self.gl.delete_texture(target.texture);
    }
  }

  /// Вершинный массив: общий квадрат (атрибут 0, углы 0..1) и буфер инстансов — атрибуты 1.. по widths чисел f32.
  pub fn instanced(&self, corners: glow::Buffer, widths: &[i32]) -> (glow::VertexArray, glow::Buffer) {
    let gl = &self.gl;
    unsafe {
      let vao = gl.create_vertex_array().expect("массив вершин");
      gl.bind_vertex_array(Some(vao));
      gl.bind_buffer(glow::ARRAY_BUFFER, Some(corners));
      gl.enable_vertex_attrib_array(0);
      gl.vertex_attrib_pointer_f32(0, 2, glow::FLOAT, false, 8, 0);
      let data = gl.create_buffer().expect("буфер");
      gl.bind_buffer(glow::ARRAY_BUFFER, Some(data));
      let stride: i32 = widths.iter().sum::<i32>() * 4;
      let mut offset = 0;
      for (i, &width) in widths.iter().enumerate() {
        let location = i as u32 + 1;
        gl.enable_vertex_attrib_array(location);
        gl.vertex_attrib_pointer_f32(location, width, glow::FLOAT, false, stride, offset);
        gl.vertex_attrib_divisor(location, 1);
        offset += width * 4;
      }
      gl.bind_vertex_array(None);
      (vao, data)
    }
  }

  pub fn upload(&self, buffer: glow::Buffer, data: &[f32]) {
    unsafe {
      self.gl.bind_buffer(glow::ARRAY_BUFFER, Some(buffer));
      self.gl.buffer_data_u8_slice(glow::ARRAY_BUFFER, bytemuck::cast_slice(data), glow::STREAM_DRAW);
    }
  }
}
