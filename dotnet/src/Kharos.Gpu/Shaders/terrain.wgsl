// Земля — перенос packages/client/src/map/terrain.frag строка в строку. Униформы GL → поля u: uScreenSize → screen,
// uCamera → camera (центр экрана в тайлах по модулю WINDOW), uZoom → zoom (пикселей на тайл), uTime → time (уже
// ступенями), uPixel → pixel (тайлов на физический пиксель), uSamples → samples, uDunes → dunes. uMap — image:
// окно мира, свёрнутое в тор: r = тип местности, g = биом, b = глубина в песках (у гор — радиус вершины), a = рельеф
// (у гор — смещение до центра вершины).

const SWAMP: i32 = 0;
const SAND: i32 = 1;
const ROCK: i32 = 2;
const MOUNTAIN: i32 = 3;

// Размер окна в тайлах (степень двойки). Координаты живут по модулю WINDOW: точности f32 хватает на любом удалении.
const WINDOW: i32 = 512;
const PERIOD: f32 = 512.0;

// «Пикселей» пиксель-арта на сторону тайла; должно совпадать с размером тайла у спрайтов.
const TEXELS_PER_TILE: f32 = 16.0;
// Тексель мельче стольких пикселей экрана — включается сглаживание, иначе сетки спорят и дают рябь.
const SMOOTH_BELOW_PIXELS: f32 = 2.0;

const ERG: i32 = 0;
const SALT_FLATS: i32 = 1;
const RED_WASTES: i32 = 2;
const MARSH: i32 = 3;

// Палитры от тёмного к светлому, по пять цветов на биом: эрг, солончаки, красные пустоши, топи.
var<private> SAND_RAMP: array<vec3f, 20> = array<vec3f, 20>(
  vec3f(0.40, 0.27, 0.08), vec3f(0.60, 0.44, 0.16), vec3f(0.76, 0.60, 0.27), vec3f(0.80, 0.65, 0.31), vec3f(0.93, 0.82, 0.52),
  vec3f(0.55, 0.52, 0.45), vec3f(0.70, 0.67, 0.58), vec3f(0.82, 0.79, 0.70), vec3f(0.86, 0.83, 0.75), vec3f(0.95, 0.93, 0.87),
  vec3f(0.36, 0.15, 0.07), vec3f(0.55, 0.26, 0.11), vec3f(0.70, 0.36, 0.16), vec3f(0.75, 0.41, 0.19), vec3f(0.90, 0.60, 0.33),
  vec3f(0.27, 0.25, 0.10), vec3f(0.40, 0.37, 0.16), vec3f(0.52, 0.48, 0.22), vec3f(0.56, 0.52, 0.25), vec3f(0.72, 0.68, 0.38)
);
var<private> ROCK_RAMP: array<vec3f, 20> = array<vec3f, 20>(
  vec3f(0.22, 0.14, 0.03), vec3f(0.31, 0.21, 0.04), vec3f(0.39, 0.27, 0.06), vec3f(0.45, 0.32, 0.08), vec3f(0.54, 0.40, 0.13),
  vec3f(0.20, 0.19, 0.18), vec3f(0.29, 0.28, 0.26), vec3f(0.37, 0.36, 0.33), vec3f(0.44, 0.43, 0.39), vec3f(0.54, 0.52, 0.47),
  vec3f(0.16, 0.06, 0.04), vec3f(0.25, 0.10, 0.06), vec3f(0.33, 0.14, 0.08), vec3f(0.40, 0.18, 0.10), vec3f(0.50, 0.25, 0.14),
  vec3f(0.13, 0.12, 0.07), vec3f(0.20, 0.19, 0.10), vec3f(0.27, 0.25, 0.13), vec3f(0.33, 0.31, 0.16), vec3f(0.42, 0.40, 0.22)
);
var<private> SWAMP_RAMP: array<vec3f, 20> = array<vec3f, 20>(
  vec3f(0.10, 0.09, 0.05), vec3f(0.15, 0.14, 0.07), vec3f(0.21, 0.20, 0.10), vec3f(0.32, 0.31, 0.13), vec3f(0.43, 0.42, 0.18),
  vec3f(0.16, 0.30, 0.32), vec3f(0.22, 0.40, 0.42), vec3f(0.30, 0.52, 0.52), vec3f(0.55, 0.72, 0.70), vec3f(0.85, 0.90, 0.86),
  vec3f(0.12, 0.06, 0.04), vec3f(0.19, 0.09, 0.05), vec3f(0.27, 0.13, 0.07), vec3f(0.40, 0.24, 0.10), vec3f(0.52, 0.33, 0.14),
  vec3f(0.04, 0.09, 0.06), vec3f(0.07, 0.14, 0.09), vec3f(0.10, 0.21, 0.13), vec3f(0.24, 0.36, 0.14), vec3f(0.38, 0.50, 0.20)
);
var<private> MUD_RAMP: array<vec3f, 5> = array<vec3f, 5>(
  vec3f(0.16, 0.11, 0.07), vec3f(0.22, 0.16, 0.10), vec3f(0.28, 0.21, 0.13), vec3f(0.34, 0.26, 0.16), vec3f(0.40, 0.31, 0.20)
);

fn sand_color(biome: i32, index: i32) -> vec3f { return SAND_RAMP[biome * 5 + index]; }
fn rock_color(biome: i32, index: i32) -> vec3f { return ROCK_RAMP[biome * 5 + index]; }
fn swamp_color(biome: i32, index: i32) -> vec3f { return SWAMP_RAMP[biome * 5 + index]; }

// Горы красятся в цвета своего биома: тени от скал, свет от песка.
fn mountain_color(biome: i32, index: i32) -> vec3f {
  if (index <= 1) { return rock_color(biome, index) * 0.75; }
  if (index == 2) { return rock_color(biome, 4); }
  return sand_color(biome, select(4, 1, index == 3)) * 0.92;
}

fn hash(q: vec2f) -> f32 {
  var p = fract(q * vec2f(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

// Шум с периодом WINDOW тайлов; scale * PERIOD должно быть целым.
fn noise(q: vec2f, scale: vec2f) -> f32 {
  let period = PERIOD * scale;
  let p = q * scale;
  let i = floor(p);
  var f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return mix(
    mix(hash(modulo2(i, period)), hash(modulo2(i + vec2f(1.0, 0.0), period)), f.x),
    mix(hash(modulo2(i + vec2f(0.0, 1.0), period)), hash(modulo2(i + vec2f(1.0, 1.0), period)), f.x),
    f.y
  );
}

fn fbm(p: vec2f, start: f32) -> f32 {
  var scale = start;
  var sum = 0.0;
  var amplitude = 0.5;
  for (var i = 0; i < 4; i++) {
    sum += amplitude * noise(p + f32(i) * 17.0, vec2f(scale));
    scale *= 2.0;
    amplitude *= 0.5;
  }
  return sum;
}

// texelFetch(uMap, cell & (WINDOW - 1), 0).
fn tile(cell: vec2i) -> vec4f {
  return textureLoad(image, cell & vec2i(WINDOW - 1), 0);
}

// Зерно текселя — таблица Grain под окном (f32 битами в RGBA8): sin так, как его считает SwiftShader у эталонов TS.
fn grain_at(texel: vec2i) -> f32 {
  let g = texel & vec2i(1023);
  let c = vec4u(round(textureLoad(image, vec2i(g.x & (WINDOW - 1), WINDOW + g.y * 2 + (g.x >> 9u)), 0) * 255.0));
  return bitcast<f32>(c.x | (c.y << 8u) | (c.z << 16u) | (c.w << 24u));
}

fn shade(value: f32, dither: f32) -> i32 {
  return clamp(i32(floor(value * 4.0 + dither)), 0, 4);
}

fn byte_of(value: f32) -> i32 {
  return i32(value * 255.0 + 0.5);
}

// x — доля скал (вместе с горами), y — доля болота, z — глубина в песках. Границы зон — между тайлами, искривлены шумом.
fn zones(p: vec2f, warp: vec2f) -> vec3f {
  let q = p - 0.5 + warp;
  let cell = vec2i(floor(q));
  let lin = fract(q);
  let f = smoothstep(vec2f(0.3), vec2f(0.7), lin);
  let weights = vec4f((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
  let t00 = tile(cell);
  let t10 = tile(cell + vec2i(1, 0));
  let t01 = tile(cell + vec2i(0, 1));
  let t11 = tile(cell + vec2i(1, 1));
  let types = vec4i(vec4f(t00.r, t10.r, t01.r, t11.r) * 255.0 + 0.5);
  return vec3f(
    dot(weights, select(vec4f(0.0), vec4f(1.0), types >= vec4i(ROCK))),
    dot(weights, select(vec4f(0.0), vec4f(1.0), types == vec4i(SWAMP))),
    mix(mix(t00.b, t10.b, lin.x), mix(t01.b, t11.b, lin.x), lin.y)
  );
}

// Совпадает с DUNE_SHIFT и Dunes в terrain.ts: барханы песчаного тайла — природные, убраны или насыпаны кистью.
const DUNE_SHIFT: u32 = 5u;

fn dune_bias(cell: vec2i) -> f32 {
  let data = tile(cell);
  if (byte_of(data.r) != SAND) { return 0.0; }
  let mode = (byte_of(data.a) >> DUNE_SHIFT) & 3;
  return select(select(0.0, 1.5, mode == 2), -3.0, mode == 1);
}

fn dune_edit(p: vec2f) -> f32 {
  let q = p - 0.5;
  let cell = vec2i(floor(q));
  let f = fract(q);
  return mix(
    mix(dune_bias(cell), dune_bias(cell + vec2i(1, 0)), f.x),
    mix(dune_bias(cell + vec2i(0, 1)), dune_bias(cell + vec2i(1, 1)), f.x),
    f.y
  );
}

// Поля барханов: индекс цвета в палитре песка или -1, если гребня в этом текселе нет.
fn dune_ridge(p: vec2f, coarse: f32, sand_depth: f32, threshold: f32) -> i32 {
  var field = noise(p + 53.0 + (coarse - 0.5) * 8.0, vec2f(0.125)) * 0.75 + noise(p + 11.0, vec2f(0.5)) * 0.25;
  field -= 1.0 - smoothstep(u.dunes.z, u.dunes.z + 0.3, sand_depth);
  field += dune_edit(p);
  if (field < threshold) { return -1; }

  let phase = p.y * 1.5 + p.x * 0.375 + noise(p, vec2f(0.75)) * 1.2;
  let ridge = floor(phase);
  let across = phase - ridge;

  let along = noise(vec2f(p.x + ridge * 8.0, modulo(ridge, 64.0)), vec2f(0.5, 1.0));
  let width = smoothstep(0.3, 0.55, along) * smoothstep(threshold, threshold + 0.06, field);

  if (across < 0.2 * width) { return 4; }
  if (across < 0.55 * width) { return 0; }
  if (across < 0.7 * width) { return 1; }
  return -1;
}

// Солончаки: расстояние до ближайшей трещины между плитами корки.
fn salt_cracks(p: vec2f) -> f32 {
  let scale = 0.75;
  let q = p * scale;
  let cell = floor(q);
  let f = fract(q);
  var nearest = 8.0;
  var second = 8.0;
  for (var y = -1; y <= 1; y++) {
    for (var x = -1; x <= 1; x++) {
      let neighbor = vec2f(f32(x), f32(y));
      let id = modulo2(cell + neighbor, vec2f(PERIOD * scale));
      let d = length(neighbor + vec2f(hash(id), hash(id + 19.3)) - f);
      if (d < nearest) {
        second = nearest;
        nearest = d;
      } else if (d < second) {
        second = d;
      }
    }
  }
  return second - nearest;
}

// Редкие пятна: 0 — нет, 1 — освещённая половина, 2 — теневая.
fn speck(p: vec2f, scale: f32, density: f32) -> i32 {
  let q = p * scale;
  let id = modulo2(floor(q), vec2f(PERIOD * scale));
  if (hash(id) > density) { return 0; }
  let delta = fract(q) - (0.25 + 0.5 * vec2f(hash(id + 7.1), hash(id + 3.3)));
  if (length(delta) > 0.1 + 0.14 * hash(id + 1.7)) { return 0; }
  return select(2, 1, delta.x + delta.y < 0.0);
}

const PEAK_RADIUS_SCALE: f32 = 4.0;
const PEAK_SEARCH: i32 = 2;

// Горы: индекс цвета в палитре гор или -1.
fn peak_shade(p: vec2f, grain: f32, dither: f32) -> i32 {
  let cell = vec2i(floor(p));
  if (byte_of(tile(cell).r) != MOUNTAIN) { return -1; }

  var best_height = 0.0;
  var value = 0.0;
  for (var y = -PEAK_SEARCH; y <= PEAK_SEARCH; y++) {
    for (var x = -PEAK_SEARCH; x <= PEAK_SEARCH; x++) {
      let other = cell + vec2i(x, y);
      let data = tile(other);
      if (byte_of(data.r) != MOUNTAIN) { continue; }

      let offset = byte_of(data.a);
      let center = vec2f(other) + 0.5 + (vec2f(f32(offset >> 4u), f32(offset & 15)) - 8.0) * 0.5;
      let radius = data.b * PEAK_RADIUS_SCALE;
      let delta = p - center;
      let dist = length(delta);
      if (dist > radius) { continue; }

      let seed = modulo2(center, vec2f(PERIOD));
      let kind = hash(seed + 3.1);
      let steep = hash(seed + 5.7);

      let streaks = floor(14.0 + radius * 8.0 + kind * 8.0);
      let angle = atan2(delta.y, delta.x) / 6.2831853 + 0.5;
      let streak = hash(vec2f(floor(angle * streaks), seed.x + seed.y * 7.0) + 0.37);
      let d = dist / (radius * (0.75 + 0.25 * streak));
      if (d > 1.0) { continue; }

      let h = (1.0 - d) * radius;
      if (h <= best_height) { continue; }
      best_height = h;

      var lit = dot(delta / max(dist, 0.001), vec2f(-0.6, -0.8));
      let crater = select(0.0, 0.25 + (kind - 0.45) * 0.5, kind > 0.45);
      if (d < crater) { lit = -lit * 0.9; }

      value = 0.5 + lit * (0.28 + steep * 0.2) + (streak - 0.5) * 0.4 + (grain - 0.5) * 0.15;
      value += (1.0 - smoothstep(0.0, 0.07, abs(d - crater))) * select(0.2, 0.3, crater > 0.0);
      value -= smoothstep(0.8, 1.0, d) * 0.3;
    }
  }
  if (best_height > 0.0) { return shade(value, dither); }
  return -1;
}

// Совпадает с CLIFF_BIT в terrain.ts.
const CLIFF_BIT: i32 = 4;

// Ярус и обрывистость кромки; у гор ярус неизвестен (-1).
fn relief(cell: vec2i) -> vec2i {
  let data = tile(cell);
  if (byte_of(data.r) == MOUNTAIN) { return vec2i(-1, 0); }
  let bits = byte_of(data.a);
  return vec2i(bits & 3, select(0, 1, (bits & CLIFF_BIT) != 0));
}

// Высота рельефа в точке и обрывиста ли кромка у ближайшего тайла.
fn height(q0: vec2f) -> vec2f {
  let q = q0 - 0.5;
  let cell = vec2i(floor(q));
  let f = smoothstep(vec2f(0.3), vec2f(0.7), fract(q));
  let r00 = relief(cell);
  let r10 = relief(cell + vec2i(1, 0));
  let r01 = relief(cell + vec2i(0, 1));
  let r11 = relief(cell + vec2i(1, 1));
  var tiers = vec4f(f32(r00.x), f32(r10.x), f32(r01.x), f32(r11.x));
  let known = max(max(tiers.x, tiers.y), max(tiers.z, tiers.w));
  tiers = select(tiers, vec4f(known), tiers < vec4f(0.0));
  let h = mix(mix(tiers.x, tiers.y, f.x), mix(tiers.z, tiers.w, f.x), f.y);
  var flag = 0;
  var top = -1.0;
  if (tiers.x > top) { top = tiers.x; flag = r00.y; }
  if (tiers.y > top) { top = tiers.y; flag = r10.y; }
  if (tiers.z > top) { top = tiers.z; flag = r01.y; }
  if (tiers.w > top) { top = tiers.w; flag = r11.y; }
  return vec2f(h, f32(flag));
}

fn level(h: f32) -> f32 { return floor(h + 0.5); }

// Совпадает с STEP_BIT в terrain.ts.
const STEP_BIT: i32 = 8;

fn flat_tier(cell: vec2i) -> i32 {
  let data = tile(cell);
  if (byte_of(data.r) == MOUNTAIN) { return -1; }
  let bits = byte_of(data.a);
  return select(bits & 3, -1, (bits & STEP_BIT) != 0);
}

const WALL: f32 = 0.7;
const WALL_STEPS: i32 = 7;

// Обрывы: x — 1 на стенке, y — глубина по стенке, z — тень у подножия, w — кромка наверху (1 светлая, -1 тёмная).
fn cliff(q: vec2f, p: vec2f) -> vec4f {
  let cell = vec2i(floor(q));
  if (flat_tier(cell) >= 0 || byte_of(tile(cell).r) == MOUNTAIN) { return vec4f(0.0); }
  let jag = vec2f(0.0, (noise(p, vec2f(4.0)) - 0.5) * 0.25 + (noise(p, vec2f(12.0)) - 0.5) * 0.1);
  let here = height(q + jag);
  let own = level(here.x);
  var result = vec4f(0.0);

  for (var i = 1; i <= WALL_STEPS; i++) {
    let s = WALL * f32(i) / f32(WALL_STEPS);
    let above = height(q + jag - vec2f(0.0, s));
    if (level(above.x) > own) {
      if (above.y > 0.5) {
        result.x = 1.0;
        result.y = s / WALL;
      }
      return result;
    }
  }
  for (var i = 1; i <= 3; i++) {
    let back = height(q + jag - vec2f(0.12, 0.08) * f32(i));
    if (level(back.x) > own && back.y > 0.5) {
      result.z = 1.0 - f32(i - 1) / 3.0;
      break;
    }
  }
  if (here.y > 0.5) {
    if (level(height(q + jag + vec2f(0.0, 0.07)).x) < own) { result.w = 1.0; }
    else if (level(height(q + jag - vec2f(0.0, 0.07)).x) < own) { result.w = -1.0; }
    else if (level(height(q + jag + vec2f(0.07, 0.0)).x) < own) { result.w = -1.0; }
    else if (level(height(q + jag - vec2f(0.07, 0.0)).x) < own) { result.w = 1.0; }
  }
  return result;
}

fn zone_warp(p: vec2f) -> vec2f {
  return (vec2f(noise(p, vec2f(2.25)), noise(p + 31.0, vec2f(2.25))) - 0.5) * 0.6;
}

fn wall_at(world_p: vec2f) -> vec4f {
  let p = (floor(world_p * TEXELS_PER_TILE) + 0.5) / TEXELS_PER_TILE;
  return cliff(p + zone_warp(p), p);
}

// Цвет карты в точке мира; detail гасит зерно и дизеринг, wall — обрыв (см. cliff).
fn terrain_color(world_p: vec2f, detail: f32, wall: vec4f) -> vec3f {
  let texel = vec2i(floor(world_p * TEXELS_PER_TILE));
  let p = (vec2f(texel) + 0.5) / TEXELS_PER_TILE;
  let dither = mix(0.5, bayer(texel), detail);
  // fract(sin(dot(mod(texel, 1024), (12.9898, 78.233))) * 43758.5453) — из таблицы (grain_at).
  let grain = mix(0.5, grain_at(texel), detail);

  let coarse = fbm(p, 1.0);
  let fine = noise(p, vec2f(8.0));

  let warp = zone_warp(p);
  let zone = zones(p, warp);
  let rock = zone.x;
  let swamp = zone.y;

  let bits = byte_of(tile(vec2i(floor(p))).g);
  var biome = bits >> 6u;
  if (coarse * 0.6 + grain * 0.4 < f32(bits & 15) / 30.0) { biome = (bits >> 4u) & 3; }

  let peak = peak_shade(p, grain, dither);

  var color: vec3f;
  if (peak >= 0) {
    color = mountain_color(biome, peak);
  } else if (rock > 0.5) {
    var value = 0.45 + (coarse - 0.5) * 0.7 + (grain - 0.5) * 0.45;
    value -= (1.0 - smoothstep(0.5, 0.85, rock)) * 0.5;
    let tier_here = flat_tier(vec2i(floor(p + warp)));
    var tier = 0.0;
    if (tier_here >= 0) { tier = f32(tier_here); } else { tier = height(p + warp).x; }
    value += (max(tier, 1.0) - 1.0) * 0.22;
    color = rock_color(biome, shade(value, dither));
  } else if (swamp > 0.5) {
    let time = floor(u.time * 5.0) / 5.0;
    let ripple = noise(p + vec2f(time * 0.12, time * 0.07), vec2f(2.5)) * 0.6
      + noise(p - vec2f(time * 0.15, 0.0), vec2f(6.0)) * 0.4;
    let scum = smoothstep(0.5, 0.62, coarse);
    var value = ripple * 0.55 + scum * 0.5;
    value -= (1.0 - smoothstep(0.5, 0.7, swamp)) * 0.3;
    color = swamp_color(biome, shade(value, dither));
  } else if (swamp > 0.04) {
    let index = shade(fine * 0.5 + (0.5 - swamp) * 1.0, dither);
    if (biome == SALT_FLATS) { color = sand_color(biome, index / 2); } else { color = MUD_RAMP[index]; }
  } else {
    color = sand_color(biome, select(2, 3, grain < 0.3));

    if (biome == SALT_FLATS) {
      let crack = salt_cracks(p + (coarse - 0.5) * 0.8);
      if (crack < 0.07) { color = sand_color(biome, select(1, 0, crack < 0.03)); }
    } else if (biome == MARSH) {
      let tuft = speck(p, 2.0, 0.55 - zone.z * 0.4);
      if (tuft > 0) { color = swamp_color(biome, select(3, 4, tuft == 1)); }
    } else {
      let ridge = dune_ridge(p, coarse, zone.z, select(u.dunes.y, u.dunes.x, biome == ERG));
      var stone = 0;
      if (biome == RED_WASTES) { stone = speck(p, 1.0, 0.22); }
      if (stone > 0) { color = rock_color(biome, select(1, 4, stone == 1)); }
      else if (ridge >= 0) { color = sand_color(biome, ridge); }
    }
  }

  if (peak < 0) {
    if (wall.x > 0.0) {
      let strata = noise(p, vec2f(6.0, 0.5));
      let value = 0.75 - wall.y * 0.8 + (strata - 0.5) * 0.7 + (grain - 0.5) * 0.2;
      if (wall.y > 0.88) { color = rock_color(biome, 0) * 0.6; } else { color = rock_color(biome, shade(value, dither)); }
    } else {
      color *= 1.0 - wall.z * 0.45;
      if (wall.w > 0.0) { color = rock_color(biome, 4); }
      else if (wall.w < 0.0) { color = rock_color(biome, 0); }
    }
  }
  return color;
}

@fragment
fn terrain(input: Screen) -> @location(0) vec4f {
  let screen_p = u.camera + (input.uv - 0.5) * u.screen / u.zoom;
  // Шаг в мире на физический пиксель — из униформы, не dpdx.
  let pixel_x = vec2f(u.pixel, 0.0);
  let pixel_y = vec2f(0.0, u.pixel);
  let texel_pixels = 1.0 / (u.pixel * TEXELS_PER_TILE);

  var color: vec3f;
  if (texel_pixels >= SMOOTH_BELOW_PIXELS) {
    color = terrain_color(screen_p, 1.0, wall_at(screen_p));
  } else {
    let detail = clamp(texel_pixels * 2.0 - 1.0, 0.0, 1.0);
    let wall = wall_at(screen_p);
    if (u.samples >= 4.0) {
      color = 0.25 * (
        terrain_color(screen_p + pixel_x * 0.125 + pixel_y * 0.375, detail, wall)
        + terrain_color(screen_p - pixel_x * 0.375 + pixel_y * 0.125, detail, wall)
        + terrain_color(screen_p + pixel_x * 0.375 - pixel_y * 0.125, detail, wall)
        + terrain_color(screen_p - pixel_x * 0.125 - pixel_y * 0.375, detail, wall)
      );
    } else if (u.samples >= 2.0) {
      color = 0.5 * (
        terrain_color(screen_p + pixel_x * 0.25 + pixel_y * 0.25, detail, wall)
        + terrain_color(screen_p - pixel_x * 0.25 - pixel_y * 0.25, detail, wall)
      );
    } else {
      color = terrain_color(screen_p, detail, wall);
    }
  }
  return vec4f(color, 1.0);
}
