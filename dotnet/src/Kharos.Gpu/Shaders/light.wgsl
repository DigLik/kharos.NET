// Свет (SPOT и BEAM из render.rs, а те — из lightingPass.ts): пятна фар и огней, ореолы, лучи с тенями.
// color, strength — uColor, uStrength. Лучу image — карта загораживающих (силуэты наземных и зданий).

const LEVELS: f32 = 5.0;

struct SpotOut {
  @builtin(position) position: vec4f,
  @location(0) texel: vec2f,
  @location(1) @interpolate(flat) radius: f32,
  @location(2) @interpolate(flat) level: f32,
}

// Инстанс: x, y в тайлах, радиус в текселях, яркость.
@vertex
fn spot_vs(@builtin(vertex_index) index: u32, @location(1) spot: vec4f) -> SpotOut {
  var out: SpotOut;
  out.radius = spot.z;
  out.level = spot.w;
  out.texel = corner(index) * out.radius * 2.0;
  out.position = clip(spot.xy + (out.texel - out.radius) / TEXELS);
  return out;
}

@fragment
fn spot_fs(input: SpotOut) -> @location(0) vec4f {
  // Производная — до ветвлений: WGSL требует для неё однородного хода.
  let texel_pixels = 1.0 / max(fwidth(input.texel.x), 0.0001);
  let texel = vec2i(floor(input.texel));
  let falloff = length(vec2f(texel) + 0.5 - input.radius) / input.radius;
  let value = pow(max(0.0, 1.0 - falloff), 2.0);
  let dither = bayer(texel);
  let stepped = min(1.0, floor(value * LEVELS + dither) / LEVELS);
  let detail = clamp(texel_pixels * 2.0 - 1.0, 0.0, 1.0);
  let alpha = mix(value, stepped, detail) * input.level * u.strength;
  return vec4f(u.color * alpha, alpha);
}

// Луч: трапеция по конусу; тень — как beamFragment в lightingPass.ts: от пикселя к источнику STEPS проверок карты
// силуэтов в серединах равных долей пути (u.samples: 10, 20 или 40 — настройка теней; по умолчанию 40, «высокая»).
// Преграда, внутри которой лежит сам пиксель, не в счёт; источник не затеняет себя на SKIP текселей.
const MAX_STEPS: i32 = 40;
const SKIP: f32 = 1.5;

struct BeamOut {
  @builtin(position) position: vec4f,
  @location(0) texel: vec2f,
  @location(1) @interpolate(flat) beam: vec4f,
  @location(2) @interpolate(flat) cone: vec4f,
}

// Инстанс: x, y, длина, яркость; cos, sin, ближняя полуширина, расширение.
@vertex
fn beam_vs(@builtin(vertex_index) index: u32, @location(1) beam: vec4f, @location(2) cone: vec4f) -> BeamOut {
  let c = corner(index);
  let along = mix(-1.0, beam.z + 1.0, c.x);
  let width = cone.z + max(along, 0.0) * cone.w + 1.0;
  let across = (c.y * 2.0 - 1.0) * width;
  var out: BeamOut;
  out.beam = beam;
  out.cone = cone;
  out.texel = cone.xy * along + vec2f(-cone.y, cone.x) * across;
  out.position = clip(beam.xy + out.texel / TEXELS);
  return out;
}

@fragment
fn beam_fs(input: BeamOut) -> @location(0) vec4f {
  let texel_pixels = 1.0 / max(fwidth(input.texel.x), 0.0001);
  let cell = vec2i(floor(input.texel));
  let texel = vec2f(cell) + 0.5;
  let direction = input.cone.xy;
  let along = dot(texel, direction);
  let across = abs(dot(texel, vec2f(-direction.y, direction.x)));
  let side = across / (input.cone.z + max(along, 0.0) * input.cone.w);
  let value = pow(max(0.0, 1.0 - length(texel) / input.beam.z), 1.5) * max(0.0, 1.0 - side * side);
  if (along <= 0.0 || value <= 0.0) { discard; }
  let start = input.beam.xy + texel / TEXELS;
  let goal = input.beam.xy + direction * SKIP / TEXELS;
  let steps = clamp(i32(u.samples), 1, MAX_STEPS);
  var outside = false;
  var shaded = false;
  for (var i = 0; i < MAX_STEPS; i++) {
    if (i >= steps) { break; }
    let at = clip(mix(start, goal, (f32(i) + 0.5) / f32(steps)));
    // В GL строка 0 цели — низ экрана (v = y·0,5 + 0,5), у WebGPU — верх: v отражается.
    let solid = textureSampleLevel(image, image_sampler, vec2f(at.x * 0.5 + 0.5, 0.5 - at.y * 0.5), 0.0).r > 0.5;
    if (!solid) {
      outside = true;
    } else if (outside) {
      shaded = true;
    }
  }
  if (shaded) { discard; }
  let dither = bayer(cell);
  let stepped = min(1.0, floor(value * LEVELS + dither) / LEVELS);
  let detail = clamp(texel_pixels * 2.0 - 1.0, 0.0, 1.0);
  let alpha = mix(value, stepped, detail) * input.beam.w * u.strength;
  return vec4f(u.color * alpha, alpha);
}
