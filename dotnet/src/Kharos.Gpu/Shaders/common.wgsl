// Общее начало всех программ (COMMON в render.rs): параметры вызова, палитры сторон, текстура вызова, камера.
// Параметры — одна структура на все программы, у каждого вызова свой слот (динамическое смещение); каждая программа
// читает только свои поля, имена — как у униформ GL без приставки u.

struct Draw {
  camera: vec2f,
  screen: vec2f,
  zoom: f32,
  pixel: f32,
  time: f32,
  samples: f32,
  dunes: vec3f,
  precipitation: f32,
  wind: vec2f,
  drift: vec2f,
  origin: vec2f,
  size: vec2f,
  color: vec3f,
  strength: f32,
  inset: vec2f,
  mode: i32,
  kind: i32,
  team: i32,
  world: i32,
  detail: i32,
  spare: i32,
}

@group(0) @binding(0) var<uniform> u: Draw;
// uPalette[12]: сторона × 3 цвета (тёмный, основной, светлый), rgb.
@group(0) @binding(1) var<uniform> palette: array<vec4f, 12>;
@group(1) @binding(0) var image: texture_2d<f32>;
@group(1) @binding(1) var image_sampler: sampler;

const TEXELS: f32 = 16.0;

// Тайл мира — в координаты отсечения: камера, логических пикселей на тайл, экран в логических пикселях.
fn clip(tile: vec2f) -> vec4f {
  let px = (tile - u.camera) * u.zoom;
  return vec4f(px.x / u.screen.x * 2.0, -px.y / u.screen.y * 2.0, 0.0, 1.0);
}

// mod из GLSL: x − y·floor(x / y). Остаток % у WGSL отбрасывает дробь к нулю и с отрицательными расходится.
fn modulo(x: f32, y: f32) -> f32 {
  return x - y * floor(x / y);
}

fn modulo2(x: vec2f, y: vec2f) -> vec2f {
  return x - y * floor(x / y);
}

// Угол квадрата полосой из четырёх вершин, как gl_VertexID в GL: (0, 0), (1, 0), (0, 1), (1, 1).
fn corner(index: u32) -> vec2f {
  return vec2f(f32(index & 1u), f32(index >> 1u));
}

// Упорядоченный дизеринг 4×4. Массив — private: константный массив с индексом во время работы naga не везде берёт.
var<private> BAYER: array<f32, 16> = array<f32, 16>(
  0.0, 8.0, 2.0, 10.0,
  12.0, 4.0, 14.0, 6.0,
  3.0, 11.0, 1.0, 9.0,
  15.0, 7.0, 13.0, 5.0
);

fn bayer(texel: vec2i) -> f32 {
  return (BAYER[(texel.y & 3) * 4 + (texel.x & 3)] + 0.5) / 16.0;
}

// Полноэкранный квадрат (TERRAIN_VS): uv — 0..1 от левого верхнего угла экрана. Ось y отсечения у WebGPU, как у GL,
// смотрит вверх, так что формула та же.
struct Screen {
  @builtin(position) position: vec4f,
  @location(0) uv: vec2f,
}

@vertex
fn fullscreen(@builtin(vertex_index) index: u32) -> Screen {
  let c = corner(index);
  return Screen(vec4f(c.x * 2.0 - 1.0, 1.0 - c.y * 2.0, 0.0, 1.0), c);
}
