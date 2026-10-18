// Спрайты из общего атласа инстансами (SPRITE_VS/FS). Инстанс: левый верхний угол и размер в тайлах от камеры,
// uv кадра, сторона, сдвиг и сила тени, серость. mode: 0 — спрайт (метка тона в альфе: 254 − тон, тон 1..3 —
// цвет стороны из палитры), 1 — тень, 2 — силуэт для карты загораживающих.

struct SpriteOut {
  @builtin(position) position: vec4f,
  @location(0) uv: vec2f,
  @location(1) @interpolate(flat) team: i32,
  @location(2) @interpolate(flat) shadow: f32,
  @location(3) @interpolate(flat) grey: f32,
}

@vertex
fn sprite_vs(@builtin(vertex_index) index: u32, @location(1) rect: vec4f, @location(2) cell: vec4f, @location(3) more: vec4f) -> SpriteOut {
  let c = corner(index);
  let shift = select(vec2f(0.0), vec2f(more.y), u.mode == 1);
  let px = (rect.xy + c * rect.zw + shift) * u.zoom;
  var out: SpriteOut;
  out.position = vec4f(px.x / u.screen.x * 2.0, -px.y / u.screen.y * 2.0, 0.0, 1.0);
  out.uv = mix(cell.xy, cell.zw, c);
  out.team = i32(more.x + 0.5);
  out.shadow = more.z;
  out.grey = more.w;
  return out;
}

@fragment
fn sprite_fs(input: SpriteOut) -> @location(0) vec4f {
  let texel = textureSampleLevel(image, image_sampler, input.uv, 0.0);
  if (texel.a < 0.5) { discard; }
  if (u.mode == 1) { return vec4f(0.0, 0.0, 0.0, input.shadow); }
  if (u.mode == 2) { return vec4f(1.0); }
  let tone = floor((1.0 - texel.a) * 255.0 + 0.5);
  var rgb = texel.rgb;
  if (tone >= 1.0 && tone <= 3.0) { rgb = palette[input.team * 3 + i32(tone) - 1].rgb; }
  if (input.grey > 0.5) { rgb = vec3f(dot(rgb, vec3f(0.3, 0.59, 0.11))); }
  return vec4f(rgb, 1.0);
}
