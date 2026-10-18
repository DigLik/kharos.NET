// Батч треугольников (BATCH_VS/FS): интерфейс (world = 0, логические пиксели) или слой мира (world = 1, тайлы через
// камеру). kind: 0 — шрифтовой атлас (альфа в r), 1 — атлас обликов, скин или мини-карта с палитрой стороны team,
// 2 — туман на мини-карте (чёрный с альфой из r, рамка картинки срезана сдвигом inset).

struct BatchOut {
  @builtin(position) position: vec4f,
  @location(0) uv: vec2f,
  @location(1) color: vec4f,
}

@vertex
fn batch_vs(@location(0) pos: vec2f, @location(1) uv: vec2f, @location(2) color: vec4f) -> BatchOut {
  var px = pos;
  if (u.world == 1) { px = (pos - u.camera) * u.zoom + u.screen / 2.0; }
  var out: BatchOut;
  out.position = vec4f(px.x / u.screen.x * 2.0 - 1.0, 1.0 - px.y / u.screen.y * 2.0, 0.0, 1.0);
  out.uv = uv;
  out.color = color;
  return out;
}

@fragment
fn batch_fs(input: BatchOut) -> @location(0) vec4f {
  if (u.kind == 2) {
    return vec4f(0.0, 0.0, 0.0, textureSampleLevel(image, image_sampler, u.inset + input.uv * (1.0 - 2.0 * u.inset), 0.0).r * input.color.a);
  }
  if (u.kind == 0) {
    return vec4f(input.color.rgb, input.color.a * textureSampleLevel(image, image_sampler, input.uv, 0.0).r);
  }
  let texel = textureSampleLevel(image, image_sampler, input.uv, 0.0);
  if (texel.a < 0.5) { discard; }
  let tone = floor((1.0 - texel.a) * 255.0 + 0.5);
  var rgb = texel.rgb;
  if (tone >= 1.0 && tone <= 3.0) { rgb = palette[u.team * 3 + i32(tone) - 1].rgb; }
  return vec4f(rgb, 1.0) * input.color;
}
