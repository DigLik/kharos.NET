// Осадки — перенос packages/client/src/weather/weather.frag. Униформы: uScreenSize → screen, uCamera → camera,
// uZoom → zoom, uTime → time, uWind → wind (тайлов в секунду), uDrift → drift (путь ветра по модулю WINDOW),
// uPrecipitation → precipitation, uPixel → pixel, uDetail → detail. uMap — image, то же окно мира: g = биом.

const WINDOW: i32 = 512;
const PERIOD: f32 = 512.0;
const TEXELS_PER_TILE: f32 = 16.0;

const DUST_COLOR_ERG: vec3f = vec3f(0.80, 0.65, 0.31);
const DUST_COLOR_SALT: vec3f = vec3f(0.0);
const DUST_COLOR_RED: vec3f = vec3f(0.70, 0.36, 0.16);
const DUST_AMOUNT: vec4f = vec4f(1.0, 0.0, 1.0, 0.0);
const RAIN_AMOUNT: vec4f = vec4f(0.0, 0.0, 0.0, 1.0);
const SNOW_AMOUNT: vec4f = vec4f(0.0, 1.0, 0.0, 0.0);
const SNOW_COLOR: vec3f = vec3f(0.96, 0.97, 1.0);
const SNOW_HAZE: vec3f = vec3f(0.82, 0.86, 0.92);
const FOG_COLOR: vec3f = vec3f(0.30, 0.37, 0.40);
const RAIN_COLOR: vec3f = vec3f(0.75, 0.85, 0.95);
const RAIN_ALPHA: f32 = 0.45;
const FOG_ALPHA: f32 = 0.55;

fn hash(q: vec2f) -> f32 {
  var p = fract(q * vec2f(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

fn noise(q: vec2f, scale: f32) -> f32 {
  let period = PERIOD * scale;
  let p = q * scale;
  let i = floor(p);
  var f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return mix(
    mix(hash(modulo2(i, vec2f(period))), hash(modulo2(i + vec2f(1.0, 0.0), vec2f(period))), f.x),
    mix(hash(modulo2(i + vec2f(0.0, 1.0), vec2f(period))), hash(modulo2(i + vec2f(1.0, 1.0), vec2f(period))), f.x),
    f.y
  );
}

// texelFetch(uMap, cell & (WINDOW - 1), 0) — байт тайла.
fn map_tile(cell: vec2i) -> vec4f {
  return textureLoad(image, cell & vec2i(WINDOW - 1), 0);
}

// sum[biome] += amount. Запись в вектор по индексу во время работы FXC (DX12) не умеет — маской.
fn add_biome(sum: vec4f, biome: i32, amount: f32) -> vec4f {
  return sum + select(vec4f(0.0), vec4f(amount), vec4i(0, 1, 2, 3) == vec4i(biome));
}

// Доли четырёх биомов в точке, плавно между тайлами.
fn biome_weights(p: vec2f) -> vec4f {
  let q = p - 0.5;
  let cell = vec2i(floor(q));
  let f = fract(q);
  var sum = vec4f(0.0);
  for (var y = 0; y <= 1; y++) {
    for (var x = 0; x <= 1; x++) {
      let weight = select(f.x, 1.0 - f.x, x == 0) * select(f.y, 1.0 - f.y, y == 0);
      let bits = i32(map_tile(cell + vec2i(x, y)).g * 255.0 + 0.5);
      let share = f32(bits & 15) / 30.0;
      sum = add_biome(sum, bits >> 6u, weight * (1.0 - share));
      sum = add_biome(sum, (bits >> 4u) & 3, weight * share);
    }
  }
  return sum;
}

fn stepped(alpha: f32, dither: f32) -> f32 {
  return clamp(floor(alpha * 6.0 + dither) / 6.0, 0.0, 1.0);
}

const DROP_CELL: f32 = 8.0;
const DROP_LIFE: f32 = 0.7;
const DROP_FALL: f32 = 0.4;

// Капли дождя: прозрачность в текселе.
fn rain_drops(texel: vec2i, amount: f32) -> f32 {
  let t = vec2f(texel) + 0.5;
  let cell = floor(t / DROP_CELL);
  let approach = vec2f(0.0, -9.0) - clamp(u.wind, vec2f(-6.0), vec2f(6.0)) * 0.9;
  let direction = normalize(approach);

  let near = vec2i(sign(fract(t / DROP_CELL) - 0.5));
  var alpha = 0.0;
  for (var y = -1; y <= 1; y++) {
    for (var x = -1; x <= 1; x++) {
      if (u.detail < 2 && ((x != 0 && x != near.x) || (y != 0 && y != near.y))) { continue; }
      let id = modulo2(cell + vec2f(f32(x), f32(y)), vec2f(PERIOD * TEXELS_PER_TILE / DROP_CELL));
      let phase = u.time / DROP_LIFE + hash(id + 0.31) * 13.0;
      let cycle = modulo(floor(phase), 256.0);
      if (hash(id + cycle * vec2f(7.13, 3.71)) > amount * 0.7) { continue; }

      let life = fract(phase);
      let landing = (cell + vec2f(f32(x), f32(y)) + 0.15 + 0.7 * vec2f(hash(id + cycle + 1.7), hash(id + cycle + 5.3))) * DROP_CELL;
      let delta = t - landing;

      if (life < DROP_FALL) {
        let head = delta - approach * (1.0 - life / DROP_FALL);
        let along = clamp(dot(head, direction), 0.0, 3.0);
        if (length(head - direction * along) < 0.6) { alpha = max(alpha, 0.6); }
        continue;
      }

      let fade = (life - DROP_FALL) / (1.0 - DROP_FALL);
      let kind = i32(map_tile(vec2i(floor(landing / TEXELS_PER_TILE))).r * 255.0 + 0.5);
      if (kind == 0) {
        if (abs(length(delta) - (0.8 + fade * 3.2)) < 0.5) { alpha = max(alpha, 0.6 * (1.0 - fade)); }
      } else if (fade < 0.5 && length(delta) < 0.6 + fade * 1.6) {
        alpha = max(alpha, 0.45 * (1.0 - fade * 2.0));
      }
    }
  }
  return alpha;
}

// Снег: до трёх слоёв хлопьев, дальние мельче и медленнее.
fn snow_flakes(texel: vec2i, amount: f32) -> f32 {
  var alpha = 0.0;
  for (var layer = 0; layer < 3; layer++) {
    if (layer < 2 - u.detail) { continue; }
    let depth = f32(layer);
    let size = select(8.0, 16.0, layer == 2);
    let sway = sin(u.time * (0.6 + depth * 0.25) + depth * 2.1) * (2.0 + depth);
    let fall = vec2f(sway, u.time * (4.0 + depth * 3.0));
    let q = vec2f(texel) + 0.5 - fall - u.drift * TEXELS_PER_TILE * (0.6 + depth * 0.2);
    let cell = floor(q / size);
    let id = modulo2(cell, vec2f(PERIOD * TEXELS_PER_TILE / size)) + depth * 17.31;
    if (hash(id) > amount * (0.35 + depth * 0.15)) { continue; }
    let flake = (cell + 0.2 + 0.6 * vec2f(hash(id + 3.1), hash(id + 7.7))) * size;
    if (length(q - flake) < 0.6 + depth * 0.35) { alpha = max(alpha, 0.55 + depth * 0.2); }
  }
  return alpha;
}

fn over(result: vec4f, color: vec3f, alpha: f32) -> vec4f {
  return vec4f(result.rgb * (1.0 - alpha) + color * alpha, result.a * (1.0 - alpha) + alpha);
}

@fragment
fn weather(input: Screen) -> @location(0) vec4f {
  let screen_p = u.camera + (input.uv - 0.5) * u.screen / u.zoom;
  let texel_pixels = 1.0 / (u.pixel * TEXELS_PER_TILE);
  let detail = clamp(texel_pixels * 2.0 - 1.0, 0.0, 1.0);

  let texel = vec2i(floor(screen_p * TEXELS_PER_TILE));
  let p = (vec2f(texel) + 0.5) / TEXELS_PER_TILE;
  let dither = mix(0.5, bayer(texel), detail);

  let biomes = biome_weights(p);
  let dust = dot(biomes, DUST_AMOUNT);
  let rain = dot(biomes, RAIN_AMOUNT);
  let snow = dot(biomes, SNOW_AMOUNT);

  let clouds = noise(p - u.drift, 0.125) * 0.6 + noise(p - u.drift * 2.0, 1.0) * 0.4;
  let cover = smoothstep(0.75 - u.precipitation * 0.7, 1.05 - u.precipitation * 0.5, clouds);

  // Результат — с предумноженной альфой (смешивается всё же SRC_ALPHA, как в render.rs).
  var result = vec4f(0.0);
  if (rain > 0.001) {
    result = over(result, FOG_COLOR, stepped((0.15 + cover * 0.3) * u.precipitation * rain * FOG_ALPHA, dither));
    if (detail > 0.0) { result = over(result, RAIN_COLOR, rain_drops(texel, u.precipitation * rain) * detail * RAIN_ALPHA); }
  }
  if (snow > 0.001) {
    result = over(result, SNOW_HAZE, stepped((0.1 + cover * 0.45) * u.precipitation * snow * 0.7, dither));
    if (detail > 0.0) { result = over(result, SNOW_COLOR, snow_flakes(texel, u.precipitation * snow) * detail); }
  }
  if (dust > 0.001) {
    let color = (biomes.x * DUST_AMOUNT.x * DUST_COLOR_ERG + biomes.y * DUST_AMOUNT.y * DUST_COLOR_SALT
      + biomes.z * DUST_AMOUNT.z * DUST_COLOR_RED) / dust;
    result = over(result, color, stepped(cover * u.precipitation * 0.85 * dust, dither));
  }
  return result;
}
