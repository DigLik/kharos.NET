//! Рендер кадра (шейдеры света — из `crates/content-bench/src/bin/render.rs`, а те — из `lightingPass.ts`):
//!
//! 1. земля — по текселям мира, 16 на тайл;
//! 2. тени и спрайты видов — инстансами, по атласу на облик, цвет стороны — палитрой игрока;
//! 3. когда атмосфера темнее дня — карта силуэтов, карта освещённости (цвет атмосферы, фары, лучи с тенями,
//!    свечения), умножение на неё и ореолы;
//! 4. слой поверх мира (выделение, частицы) и интерфейс — батчами треугольников.

use crate::atlas::Look;
use crate::gl::{Gl, Target};
use crate::ui::{Batch, FontAtlas, Paint, ATLAS_SIZE};
use glow::HasContext as _;

/// Текселей на тайл.
pub const TEXELS: f32 = 16.0;
/// Сторон в палитре: по три цвета (тёмный, основной, светлый) на игрока.
pub const TEAMS: usize = 8;

/// Камера: центр в тайлах и логических пикселей на тайл.
#[derive(Clone, Copy, Debug)]
pub struct View {
  pub x: f32,
  pub y: f32,
  pub zoom: f32,
  /// Экран в логических пикселях и физических пикселей на логический.
  pub screen: [f32; 2],
  pub scale: f32,
}

impl View {
  pub fn to_screen(&self, x: f32, y: f32) -> [f32; 2] {
    [(x - self.x) * self.zoom + self.screen[0] / 2.0, (y - self.y) * self.zoom + self.screen[1] / 2.0]
  }

  pub fn to_world(&self, x: f32, y: f32) -> [f32; 2] {
    [(x - self.screen[0] / 2.0) / self.zoom + self.x, (y - self.screen[1] / 2.0) / self.zoom + self.y]
  }

  /// Видимая часть мира: левый верхний и правый нижний угол в тайлах.
  pub fn bounds(&self, margin: f32) -> [f32; 4] {
    let [left, top] = self.to_world(0.0, 0.0);
    let [right, bottom] = self.to_world(self.screen[0], self.screen[1]);
    [left - margin, top - margin, right + margin, bottom + margin]
  }
}

/// Что рисовать в кадре.
#[derive(Default)]
pub struct Frame {
  /// По облику: инстансы x, y, ячейка атласа, сторона.
  pub sprites: Vec<Vec<f32>>,
  /// Фары: x, y, радиус в текселях, яркость.
  pub spots: Vec<f32>,
  /// Ореолы фар, тот же формат.
  pub blooms: Vec<f32>,
  /// Лучи: x, y, длина, яркость, cos, sin, ближняя полуширина, расширение.
  pub beams: Vec<f32>,
  /// Цвет света сцены (1, 1, 1 — день) и сила огней 0..1.
  pub ambient: [f32; 3],
  pub lamps: f32,
  /// Палитры сторон: TEAMS × 3 цвета.
  pub palette: Vec<f32>,
  /// Земля: цвета песка, камня и границы поля и её размер (−size..size, 0 — без границы).
  pub ground: [[f32; 3]; 3],
  pub field: f32,
}

const COMMON: &str = r#"
uniform vec2 uCamera;
uniform float uZoom;
uniform vec2 uScreen;
const float TEXELS = 16.0;
vec4 clip(vec2 tile) {
  vec2 px = (tile - uCamera) * uZoom;
  return vec4(px.x / uScreen.x * 2.0, -px.y / uScreen.y * 2.0, 0.0, 1.0);
}
"#;

const LAND_VS: &str = r#"
out vec2 vTile;
void main() {
  vec2 corner = vec2(gl_VertexID & 1, gl_VertexID >> 1) * 2.0 - 1.0;
  vTile = uCamera + vec2(corner.x, -corner.y) * uScreen / 2.0 / uZoom;
  gl_Position = vec4(corner, 0.0, 1.0);
}
"#;

// Земля: шум по тайлам даёт пятна камня, у каждого текселя — своя крупинка; за краем поля — темнее.
const LAND_FS: &str = r#"
in vec2 vTile;
uniform vec3 uSand;
uniform vec3 uRock;
uniform vec3 uEdge;
uniform float uField;
out vec4 color;
float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
float noise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
void main() {
  vec2 texel = floor(vTile * TEXELS);
  vec2 at = (texel + 0.5) / TEXELS;
  float patches = noise(at / 9.0) * 0.65 + noise(at / 3.0) * 0.35;
  float rock = smoothstep(0.58, 0.62, patches);
  float grain = hash(texel);
  vec3 base = mix(uSand, uRock, rock);
  base *= 0.92 + 0.1 * grain + 0.06 * (noise(at / 1.5) - 0.5);
  if (uField > 0.0 && (abs(at.x) > uField || abs(at.y) > uField)) base = mix(base, uEdge, 0.6);
  color = vec4(base, 1.0);
}
"#;

const SPRITE_VS: &str = r#"
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec4 aSprite;
uniform vec2 uGrid;
uniform float uFrame;
uniform vec2 uShift;
out vec2 vUv;
flat out int vTeam;
void main() {
  vec2 tile = aSprite.xy + (aCorner - 0.5) * uFrame + uShift;
  gl_Position = clip(tile);
  vec2 cell = vec2(mod(aSprite.z, uGrid.x), floor(aSprite.z / uGrid.x));
  vUv = (cell + aCorner) / uGrid;
  vTeam = int(aSprite.w);
}
"#;

// uMode: 0 — спрайт (метка тона в альфе: 254 − тон — цвет стороны из палитры), 1 — тень, 2 — силуэт.
const SPRITE_FS: &str = r#"
in vec2 vUv;
flat in int vTeam;
uniform sampler2D uAtlas;
uniform vec3 uPalette[24];
uniform int uMode;
uniform float uShadow;
out vec4 color;
void main() {
  vec4 texel = texture(uAtlas, vUv);
  if (texel.a < 0.5) discard;
  if (uMode == 1) { color = vec4(0.0, 0.0, 0.0, uShadow); return; }
  if (uMode == 2) { color = vec4(1.0); return; }
  float mark = floor(texel.a * 255.0 + 0.5);
  vec3 rgb = mark < 254.5 ? uPalette[vTeam * 3 + int(254.0 - mark)] : texel.rgb;
  color = vec4(rgb, 1.0);
}
"#;

const SPOT_VS: &str = r#"
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec4 aSpot;
out vec2 vTexel;
flat out float vRadius;
flat out float vLevel;
void main() {
  vRadius = aSpot.z;
  vLevel = aSpot.w;
  vTexel = aCorner * vRadius * 2.0;
  vec2 tile = aSpot.xy + (vTexel - vRadius) / TEXELS;
  gl_Position = clip(tile);
}
"#;

const SPOT_FS: &str = r#"
in vec2 vTexel;
flat in float vRadius;
flat in float vLevel;
uniform vec3 uColor;
uniform float uStrength;
out vec4 finalColor;
const float LEVELS = 5.0;
const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
void main() {
  ivec2 texel = ivec2(floor(vTexel));
  float falloff = length(vec2(texel) + 0.5 - vRadius) / vRadius;
  float value = pow(max(0.0, 1.0 - falloff), 2.0);
  float dither = (BAYER[(texel.y & 3) * 4 + (texel.x & 3)] + 0.5) / 16.0;
  float stepped = min(1.0, floor(value * LEVELS + dither) / LEVELS);
  float texelPixels = 1.0 / max(fwidth(vTexel.x), 0.0001);
  float detail = clamp(texelPixels * 2.0 - 1.0, 0.0, 1.0);
  float alpha = mix(value, stepped, detail) * vLevel * uStrength;
  finalColor = vec4(uColor * alpha, alpha);
}
"#;

// Быстрый луч: трапеция по конусу, тень — раз в SPACING текселей пути до источника, с ранним выходом.
const BEAM_VS: &str = r#"
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec4 aBeam;
layout(location = 2) in vec4 aCone;
out vec2 vTexel;
flat out vec4 vBeam;
flat out vec4 vCone;
void main() {
  vBeam = aBeam;
  vCone = aCone;
  float along = mix(-1.0, aBeam.z + 1.0, aCorner.x);
  float width = aCone.z + max(along, 0.0) * aCone.w + 1.0;
  float across = (aCorner.y * 2.0 - 1.0) * width;
  vTexel = aCone.xy * along + vec2(-aCone.y, aCone.x) * across;
  gl_Position = clip(aBeam.xy + vTexel / TEXELS);
}
"#;

const BEAM_FS: &str = r#"
in vec2 vTexel;
flat in vec4 vBeam;
flat in vec4 vCone;
uniform vec3 uColor;
uniform float uStrength;
uniform sampler2D uOccluders;
out vec4 finalColor;
const float LEVELS = 5.0;
const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
const float SPACING = 3.0;
const int MAX_STEPS = 64;
const float SKIP = 1.5;
void main() {
  ivec2 cell = ivec2(floor(vTexel));
  vec2 texel = vec2(cell) + 0.5;
  vec2 direction = vCone.xy;
  float along = dot(texel, direction);
  float across = abs(dot(texel, vec2(-direction.y, direction.x)));
  float side = across / (vCone.z + max(along, 0.0) * vCone.w);
  float value = pow(max(0.0, 1.0 - length(texel) / vBeam.z), 1.5) * max(0.0, 1.0 - side * side);
  if (along <= 0.0 || value <= 0.0) discard;
  vec2 from = vBeam.xy + texel / TEXELS;
  vec2 to = vBeam.xy + direction * SKIP / TEXELS;
  int steps = max(1, int(ceil(max(length(texel) - SKIP, 0.0) / SPACING)));
  bool outside = false;
  for (int i = 0; i < MAX_STEPS; i++) {
    if (i >= steps) break;
    vec4 at = clip(mix(from, to, float(i + 1) / float(steps)));
    bool solid = textureLod(uOccluders, at.xy * 0.5 + 0.5, 0.0).r > 0.5;
    if (!solid) outside = true;
    else if (outside) discard;
  }
  float dither = (BAYER[(cell.y & 3) * 4 + (cell.x & 3)] + 0.5) / 16.0;
  float stepped = min(1.0, floor(value * LEVELS + dither) / LEVELS);
  float texelPixels = 1.0 / max(fwidth(vTexel.x), 0.0001);
  float detail = clamp(texelPixels * 2.0 - 1.0, 0.0, 1.0);
  float alpha = mix(value, stepped, detail) * vBeam.w * uStrength;
  finalColor = vec4(uColor * alpha, alpha);
}
"#;

const BLIT_VS: &str = r#"
out vec2 vUv;
void main() {
  vec2 corner = vec2(gl_VertexID & 1, gl_VertexID >> 1);
  vUv = corner;
  gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}
"#;

const BLIT_FS: &str = r#"
in vec2 vUv;
uniform sampler2D uTexture;
out vec4 color;
void main() { color = texture(uTexture, vUv); }
"#;

// Батч: интерфейс (uWorld = 0, логические пиксели) или слой мира (uWorld = 1, тайлы через камеру). uKind 0 —
// шрифтовой атлас (альфа), 1 — атлас вида с палитрой стороны.
const BATCH_VS: &str = r#"
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
uniform int uWorld;
out vec2 vUv;
out vec4 vColor;
void main() {
  vUv = aUv;
  vColor = aColor;
  vec2 px = uWorld == 1 ? (aPos - uCamera) * uZoom + uScreen / 2.0 : aPos;
  gl_Position = vec4(px.x / uScreen.x * 2.0 - 1.0, 1.0 - px.y / uScreen.y * 2.0, 0.0, 1.0);
}
"#;

const BATCH_FS: &str = r#"
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uAtlas;
uniform int uKind;
uniform int uTeam;
uniform vec3 uPalette[24];
out vec4 color;
void main() {
  if (uKind == 0) {
    color = vec4(vColor.rgb, vColor.a * texture(uAtlas, vUv).r);
    return;
  }
  vec4 texel = texture(uAtlas, vUv);
  if (texel.a < 0.5) discard;
  float mark = floor(texel.a * 255.0 + 0.5);
  vec3 rgb = mark < 254.5 ? uPalette[uTeam * 3 + int(254.0 - mark)] : texel.rgb;
  color = vec4(rgb, 1.0) * vColor;
}
"#;

struct Programs {
  land: glow::Program,
  sprite: glow::Program,
  spot: glow::Program,
  beam: glow::Program,
  blit: glow::Program,
  batch: glow::Program,
}

/// Атлас облика на видеокарте.
struct Sheet {
  texture: glow::Texture,
  grid: [f32; 2],
  frame: f32,
}

pub struct Renderer {
  pub gl: Gl,
  programs: Programs,
  sprite_vao: glow::VertexArray,
  sprite_data: glow::Buffer,
  spot_vao: glow::VertexArray,
  spot_data: glow::Buffer,
  beam_vao: glow::VertexArray,
  beam_data: glow::Buffer,
  empty_vao: glow::VertexArray,
  batch_vao: glow::VertexArray,
  batch_vertices: glow::Buffer,
  batch_indices: glow::Buffer,
  font: glow::Texture,
  font_generation: u64,
  sheets: Vec<Sheet>,
  occluders: Option<Target>,
  lightmap: Option<Target>,
  size: [i32; 2],
}

const LIGHT_COLOR: [f32; 3] = [0xa8 as f32 / 255.0, 0xd0 as f32 / 255.0, 1.0];
const BLOOM_COLOR: [f32; 3] = [0x5a as f32 / 255.0, 0xa9 as f32 / 255.0, 1.0];
const SHADOW_SHIFT: f32 = 2.0 / TEXELS;

impl Renderer {
  pub fn new(gl: glow::Context) -> Result<Self, String> {
    let gl = Gl::new(gl);
    let with = |source: &str| format!("{COMMON}{source}");
    let programs = Programs { land: gl.program(&with(LAND_VS), &with(LAND_FS))?, sprite: gl.program(&with(SPRITE_VS), &with(SPRITE_FS))?, spot: gl.program(&with(SPOT_VS), &with(SPOT_FS))?, beam: gl.program(&with(BEAM_VS), &with(BEAM_FS))?, blit: gl.program(BLIT_VS, BLIT_FS)?, batch: gl.program(&with(BATCH_VS), &with(BATCH_FS))? };
    unsafe {
      let raw = &gl.gl;
      let corners = raw.create_buffer()?;
      raw.bind_buffer(glow::ARRAY_BUFFER, Some(corners));
      let quad: [f32; 8] = [0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0];
      raw.buffer_data_u8_slice(glow::ARRAY_BUFFER, bytemuck::cast_slice(&quad), glow::STATIC_DRAW);
      let (sprite_vao, sprite_data) = gl.instanced(corners, &[4]);
      let (spot_vao, spot_data) = gl.instanced(corners, &[4]);
      let (beam_vao, beam_data) = gl.instanced(corners, &[4, 4]);
      let empty_vao = raw.create_vertex_array()?;
      let batch_vao = raw.create_vertex_array()?;
      let batch_vertices = raw.create_buffer()?;
      let batch_indices = raw.create_buffer()?;
      raw.bind_vertex_array(Some(batch_vao));
      raw.bind_buffer(glow::ARRAY_BUFFER, Some(batch_vertices));
      raw.bind_buffer(glow::ELEMENT_ARRAY_BUFFER, Some(batch_indices));
      let stride = std::mem::size_of::<crate::ui::Vertex>() as i32;
      raw.enable_vertex_attrib_array(0);
      raw.vertex_attrib_pointer_f32(0, 2, glow::FLOAT, false, stride, 0);
      raw.enable_vertex_attrib_array(1);
      raw.vertex_attrib_pointer_f32(1, 2, glow::FLOAT, false, stride, 8);
      raw.enable_vertex_attrib_array(2);
      raw.vertex_attrib_pointer_f32(2, 4, glow::UNSIGNED_BYTE, true, stride, 16);
      raw.bind_vertex_array(None);
      let font = gl.texture([ATLAS_SIZE as i32; 2], glow::R8, glow::RED, None);
      raw.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MIN_FILTER, glow::LINEAR as i32);
      raw.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MAG_FILTER, glow::LINEAR as i32);
      Ok(Self { gl, programs, sprite_vao, sprite_data, spot_vao, spot_data, beam_vao, beam_data, empty_vao, batch_vao, batch_vertices, batch_indices, font, font_generation: 0, sheets: Vec::new(), occluders: None, lightmap: None, size: [0, 0] })
    }
  }

  /// Загружает атласы обликов; номер облика — его место в списке.
  pub fn load_looks(&mut self, looks: &[Look]) {
    for sheet in self.sheets.drain(..) {
      unsafe { self.gl.gl.delete_texture(sheet.texture) };
    }
    for look in looks {
      let texture = self.gl.texture(look.size(), glow::RGBA8, glow::RGBA, Some(&look.pixels));
      self.sheets.push(Sheet { texture, grid: [look.columns as f32, look.rows as f32], frame: look.frame as f32 / TEXELS });
    }
  }

  unsafe fn camera(&self, program: glow::Program, view: &View) {
    let gl = &self.gl.gl;
    gl.use_program(Some(program));
    gl.uniform_2_f32(gl.get_uniform_location(program, "uCamera").as_ref(), view.x, view.y);
    gl.uniform_1_f32(gl.get_uniform_location(program, "uZoom").as_ref(), view.zoom);
    gl.uniform_2_f32(gl.get_uniform_location(program, "uScreen").as_ref(), view.screen[0], view.screen[1]);
  }

  unsafe fn sprites(&self, view: &View, frame: &Frame, mode: i32, shift: f32, shadow: f32) {
    let gl = &self.gl.gl;
    let program = self.programs.sprite;
    self.camera(program, view);
    let uniform = |name: &str| gl.get_uniform_location(program, name);
    gl.uniform_1_i32(uniform("uMode").as_ref(), mode);
    gl.uniform_2_f32(uniform("uShift").as_ref(), shift, shift);
    gl.uniform_1_f32(uniform("uShadow").as_ref(), shadow);
    gl.uniform_3_f32_slice(uniform("uPalette").as_ref(), &frame.palette);
    gl.bind_vertex_array(Some(self.sprite_vao));
    for (index, instances) in frame.sprites.iter().enumerate() {
      let Some(sheet) = self.sheets.get(index) else { continue };
      if instances.is_empty() {
        continue;
      }
      self.gl.upload(self.sprite_data, instances);
      gl.uniform_2_f32(uniform("uGrid").as_ref(), sheet.grid[0], sheet.grid[1]);
      gl.uniform_1_f32(uniform("uFrame").as_ref(), sheet.frame);
      gl.bind_texture(glow::TEXTURE_2D, Some(sheet.texture));
      gl.draw_arrays_instanced(glow::TRIANGLE_STRIP, 0, 4, (instances.len() / 4) as i32);
    }
  }

  unsafe fn spots(&self, view: &View, data: &[f32], color: [f32; 3], strength: f32) {
    if data.is_empty() {
      return;
    }
    let gl = &self.gl.gl;
    let program = self.programs.spot;
    self.gl.upload(self.spot_data, data);
    self.camera(program, view);
    gl.uniform_3_f32(gl.get_uniform_location(program, "uColor").as_ref(), color[0], color[1], color[2]);
    gl.uniform_1_f32(gl.get_uniform_location(program, "uStrength").as_ref(), strength);
    gl.bind_vertex_array(Some(self.spot_vao));
    gl.draw_arrays_instanced(glow::TRIANGLE_STRIP, 0, 4, (data.len() / 4) as i32);
  }

  /// Цели ночи — в размер экрана в физических пикселях.
  fn targets(&mut self, size: [i32; 2]) {
    if self.lightmap.as_ref().is_some_and(|target| target.size == size) {
      return;
    }
    for target in [self.lightmap.take(), self.occluders.take()].into_iter().flatten() {
      self.gl.drop_target(&target);
    }
    self.lightmap = Some(self.gl.target(size, glow::RGBA8, glow::RGBA));
    self.occluders = Some(self.gl.target(size, glow::R8, glow::RED));
  }

  /// Кадр мира: земля, тени, спрайты, свет.
  pub fn draw_world(&mut self, view: &View, frame: &Frame, size: [i32; 2]) {
    self.size = size;
    let night = frame.ambient.iter().any(|&channel| channel < 0.97);
    if night {
      self.targets(size);
    }
    let gl = &self.gl.gl;
    unsafe {
      gl.bind_framebuffer(glow::FRAMEBUFFER, None);
      gl.viewport(0, 0, size[0], size[1]);
      gl.active_texture(glow::TEXTURE0);
      gl.disable(glow::BLEND);
      let land = self.programs.land;
      self.camera(land, view);
      let uniform = |name: &str| gl.get_uniform_location(land, name);
      for (name, color) in ["uSand", "uRock", "uEdge"].into_iter().zip(frame.ground) {
        gl.uniform_3_f32(uniform(name).as_ref(), color[0], color[1], color[2]);
      }
      gl.uniform_1_f32(uniform("uField").as_ref(), frame.field);
      gl.bind_vertex_array(Some(self.empty_vao));
      gl.draw_arrays(glow::TRIANGLE_STRIP, 0, 4);

      gl.enable(glow::BLEND);
      gl.blend_func(glow::SRC_ALPHA, glow::ONE_MINUS_SRC_ALPHA);
      let daylight = frame.ambient.iter().sum::<f32>() / 3.0;
      self.sprites(view, frame, 1, SHADOW_SHIFT, 0.4 * daylight.clamp(0.3, 1.0));
      gl.disable(glow::BLEND);
      self.sprites(view, frame, 0, 0.0, 0.0);
      if !night {
        return;
      }
      let (occluders, lightmap) = (self.occluders.as_ref().expect("силуэты"), self.lightmap.as_ref().expect("свет"));
      gl.bind_framebuffer(glow::FRAMEBUFFER, Some(occluders.framebuffer));
      gl.clear_color(0.0, 0.0, 0.0, 0.0);
      gl.clear(glow::COLOR_BUFFER_BIT);
      self.sprites(view, frame, 2, 0.0, 0.0);

      gl.enable(glow::BLEND);
      gl.blend_func(glow::ONE, glow::ONE);
      gl.bind_framebuffer(glow::FRAMEBUFFER, Some(lightmap.framebuffer));
      gl.clear_color(frame.ambient[0], frame.ambient[1], frame.ambient[2], 1.0);
      gl.clear(glow::COLOR_BUFFER_BIT);
      if frame.lamps > 0.0 {
        self.spots(view, &frame.spots, LIGHT_COLOR, frame.lamps);
        if !frame.beams.is_empty() {
          self.gl.upload(self.beam_data, &frame.beams);
          let beam = self.programs.beam;
          self.camera(beam, view);
          gl.uniform_3_f32(gl.get_uniform_location(beam, "uColor").as_ref(), LIGHT_COLOR[0], LIGHT_COLOR[1], LIGHT_COLOR[2]);
          gl.uniform_1_f32(gl.get_uniform_location(beam, "uStrength").as_ref(), frame.lamps);
          gl.bind_texture(glow::TEXTURE_2D, Some(occluders.texture));
          gl.bind_vertex_array(Some(self.beam_vao));
          gl.draw_arrays_instanced(glow::TRIANGLE_STRIP, 0, 4, (frame.beams.len() / 8) as i32);
        }
      }
      gl.bind_framebuffer(glow::FRAMEBUFFER, None);
      gl.blend_func(glow::DST_COLOR, glow::ZERO);
      gl.use_program(Some(self.programs.blit));
      gl.bind_texture(glow::TEXTURE_2D, Some(lightmap.texture));
      gl.bind_vertex_array(Some(self.empty_vao));
      gl.draw_arrays(glow::TRIANGLE_STRIP, 0, 4);
      gl.blend_func(glow::ONE, glow::ONE);
      self.spots(view, &frame.blooms, BLOOM_COLOR, frame.lamps * 0.6);
      gl.disable(glow::BLEND);
    }
  }

  /// Батч поверх: world — в тайлах через камеру, иначе — в логических пикселях экрана.
  pub fn draw_batch(&mut self, batch: &Batch, font: &FontAtlas, view: &View, world: bool, palette: &[f32]) {
    if batch.indices.is_empty() {
      return;
    }
    let gl = &self.gl.gl;
    unsafe {
      gl.bind_framebuffer(glow::FRAMEBUFFER, None);
      gl.viewport(0, 0, self.size[0], self.size[1]);
      gl.enable(glow::BLEND);
      gl.blend_func_separate(glow::SRC_ALPHA, glow::ONE_MINUS_SRC_ALPHA, glow::ONE, glow::ONE_MINUS_SRC_ALPHA);
      gl.active_texture(glow::TEXTURE0);
      gl.bind_texture(glow::TEXTURE_2D, Some(self.font));
      if self.font_generation != font.generation {
        gl.pixel_store_i32(glow::UNPACK_ALIGNMENT, 1);
        gl.tex_image_2d(glow::TEXTURE_2D, 0, glow::R8 as i32, ATLAS_SIZE as i32, ATLAS_SIZE as i32, 0, glow::RED, glow::UNSIGNED_BYTE, glow::PixelUnpackData::Slice(Some(&font.pixels)));
        self.font_generation = font.generation;
      }
      let program = self.programs.batch;
      self.camera(program, view);
      let uniform = |name: &str| gl.get_uniform_location(program, name);
      gl.uniform_1_i32(uniform("uWorld").as_ref(), world as i32);
      gl.uniform_3_f32_slice(uniform("uPalette").as_ref(), palette);
      gl.bind_vertex_array(Some(self.batch_vao));
      gl.bind_buffer(glow::ARRAY_BUFFER, Some(self.batch_vertices));
      gl.buffer_data_u8_slice(glow::ARRAY_BUFFER, bytemuck::cast_slice(&batch.vertices), glow::STREAM_DRAW);
      gl.bind_buffer(glow::ELEMENT_ARRAY_BUFFER, Some(self.batch_indices));
      gl.buffer_data_u8_slice(glow::ELEMENT_ARRAY_BUFFER, bytemuck::cast_slice(&batch.indices), glow::STREAM_DRAW);
      for (index, &(start, paint)) in batch.segments.iter().enumerate() {
        let end = batch.segments.get(index + 1).map_or(batch.indices.len(), |next| next.0);
        match paint {
          Paint::Font => {
            gl.uniform_1_i32(uniform("uKind").as_ref(), 0);
            gl.bind_texture(glow::TEXTURE_2D, Some(self.font));
          }
          Paint::Sprite { look, team } => {
            let Some(sheet) = self.sheets.get(look) else { continue };
            gl.uniform_1_i32(uniform("uKind").as_ref(), 1);
            gl.uniform_1_i32(uniform("uTeam").as_ref(), team as i32);
            gl.bind_texture(glow::TEXTURE_2D, Some(sheet.texture));
          }
        }
        gl.draw_elements(glow::TRIANGLES, (end - start) as i32, glow::UNSIGNED_INT, (start * 4) as i32);
      }
      gl.bind_vertex_array(None);
      gl.disable(glow::BLEND);
    }
  }

  /// Кадр в PNG.
  pub fn shot(&self, path: &str, size: [u32; 2]) {
    let [width, height] = size;
    let mut pixels = vec![0u8; (width * height * 4) as usize];
    unsafe {
      self.gl.gl.bind_framebuffer(glow::FRAMEBUFFER, None);
      self.gl.gl.read_pixels(0, 0, width as i32, height as i32, glow::RGBA, glow::UNSIGNED_BYTE, glow::PixelPackData::Slice(Some(&mut pixels)));
    }
    let rows: Vec<u8> = pixels.chunks_exact((width * 4) as usize).rev().flatten().copied().collect();
    let file = match std::fs::File::create(path) {
      Ok(file) => file,
      Err(error) => return eprintln!("{path}: {error}"),
    };
    let mut encoder = png::Encoder::new(std::io::BufWriter::new(file), width, height);
    encoder.set_color(png::ColorType::Rgba);
    if let Err(error) = encoder.write_header().and_then(|mut writer| writer.write_image_data(&rows)) {
      eprintln!("{path}: {error}");
    }
  }
}
