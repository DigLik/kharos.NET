// Полноэкранные проходы: туман войны (FOG_FS, fogPass.ts), умножение на карту освещённости (BLIT) и перенос
// готового кадра в окно.

// Туман: тайл — тексель, сглаживание между ними даёт мягкий край обзора; за рамкой — растянутая рамка.
// camera — центр экрана в тайлах, zoom — физических пикселей на тайл, screen — экран в физических пикселях,
// origin — левый верхний угол картинки обзора в тайлах, size — её размер.
@fragment
fn fog(input: Screen) -> @location(0) vec4f {
  let tile = u.camera + (input.uv - 0.5) * u.screen / u.zoom;
  let alpha = textureSampleLevel(image, image_sampler, (tile - u.origin) / u.size, 0.0).r;
  return vec4f(0.0, 0.0, 0.0, alpha);
}

// Цель того же размера, что экран: тексель под пикселем — тот же, что брал texture() с ближайшим соседом.
@fragment
fn blit(input: Screen) -> @location(0) vec4f {
  return textureLoad(image, vec2i(floor(input.position.xy)), 0);
}

// Перенос кадра в окно: непрозрачно — окно смешивать с рабочим столом нечего.
@fragment
fn present(input: Screen) -> @location(0) vec4f {
  return vec4f(textureLoad(image, vec2i(floor(input.position.xy)), 0).rgb, 1.0);
}
