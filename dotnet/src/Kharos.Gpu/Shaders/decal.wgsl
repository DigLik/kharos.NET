// Следы на земле (DECAL, decalsPass.ts): отрезок шириной width, всё решается для пикселя мира целиком, по его центру.

struct DecalOut {
  @builtin(position) position: vec4f,
  @location(0) local: vec2f,
  @location(1) world: vec2f,
  @location(2) cell: vec4f,
  @location(3) tint: vec4f,
  @location(4) fade: vec3f,
}

// Инстанс: отрезок, ширина, кадр атласа (ширина 0 — белый), цвет и непрозрачность, сколько осталось, плотность, зерно.
@vertex
fn decal_vs(@builtin(vertex_index) index: u32, @location(1) line: vec4f, @location(2) width: f32, @location(3) cell: vec4f,
  @location(4) tint: vec4f, @location(5) fade: vec3f) -> DecalOut {
  let c = corner(index);
  let span = line.zw - line.xy;
  let size = max(length(span), 1e-4);
  let along = span / size;
  let across = vec2f(-along.y, along.x);
  // Четырёхугольник шире на пиксель мира с каждой стороны: пиксель, чей центр внутри, закрашивается целиком.
  let pad = 1.0 / TEXELS;
  let local = vec2f(mix(-pad / size, 1.0 + pad / size, c.x), mix(-pad / width, 1.0 + pad / width, c.y));
  let tile = line.xy + span * local.x + across * (local.y - 0.5) * width;
  var out: DecalOut;
  out.local = local;
  out.world = tile * TEXELS;
  out.cell = cell;
  out.tint = tint;
  out.fade = fade;
  out.position = clip(tile);
  return out;
}

// Случайное число 0..1 для пикселя мира и зерна: у каждого следа пиксели гаснут в свой черёд.
fn grain(cell: vec2f, seed: f32) -> f32 {
  var p = fract((cell.xyx + seed) * vec3f(0.1031, 0.1030, 0.0973));
  p += dot(p, p.yzx + 33.33);
  return fract((p.x + p.y) * p.z);
}

// inverse(mat2) из GLSL.
fn inverse2(m: mat2x2f) -> mat2x2f {
  let det = m[0][0] * m[1][1] - m[1][0] * m[0][1];
  return mat2x2f(m[1][1], -m[0][1], -m[1][0], m[0][0]) * (1.0 / det);
}

@fragment
fn decal_fs(input: DecalOut) -> @location(0) vec4f {
  // dpdy у WebGPU — по оси вниз, у GL — вверх: знак меняется у обеих матриц и сокращается.
  let to_local = mat2x2f(dpdx(input.local), dpdy(input.local)) * inverse2(mat2x2f(dpdx(input.world), dpdy(input.world)));
  let cell = floor(input.world);
  let local = input.local + to_local * (cell + 0.5 - input.world);
  if (any(local < vec2f(0.0)) || any(local > vec2f(1.0))) { discard; }
  var texel = vec4f(1.0);
  if (input.cell.z > 0.0) { texel = textureSampleLevel(image, image_sampler, input.cell.xy + local * input.cell.zw, 0.0); }
  if (texel.a < 0.5) { discard; }
  // Дизеринг шумом: доля закрашенных пикселей — плотность, умноженная на то, сколько осталось до исчезновения.
  if (input.fade.x * input.fade.y <= grain(cell, input.fade.z)) { discard; }
  return vec4f(texel.rgb * input.tint.rgb, 1.0) * input.tint.a;
}
