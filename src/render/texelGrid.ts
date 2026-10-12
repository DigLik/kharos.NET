import { SCREEN_VERTEX, createProgram, drawQuad, type Program, type Texture } from '../gl'
import type { Frame, View } from './renderer'

/** Пикселей местности (текселей пиксель-арта) на тайл. */
export const TEXELS_PER_TILE = 16

/**
 * Карта, в которую проход рисует вместо экрана, — в сетке местности, по пикселю на тексель пиксель-арта. Вблизи
 * в ней в разы меньше пикселей, чем на экране, а на экран она переносится без сглаживания: что одинаково в пределах
 * текселя (земля, свет, погода), выглядит так же, как нарисованное прямо на экране. Издалека, когда тексель мельче
 * closeFrom пикселей экрана, карта совпадает с экраном.
 */
export interface TexelGrid {
  /** Карта в сетке местности; иначе — в пикселях экрана. */
  close: boolean
  width: number
  height: number
  /** Пикселей карты на тайл. */
  perTile: number
  /** Тайлов мира на физический пиксель экрана — не карты. */
  pixel: number
  /** Середина карты в тайлах мира: вблизи — узел сетки местности, издалека — сама камера. */
  centerX: number
  centerY: number
  /** Юниформы для рисования в карту в координатах тайлов от камеры, как View кадра. */
  view: View
}

export function createTexelGrid(): TexelGrid {
  return {
    close: false,
    width: 1,
    height: 1,
    perTile: 1,
    pixel: 1,
    centerX: 0,
    centerY: 0,
    view: { uScreenSize: new Float32Array(2), uScale: new Float32Array(2), uOffset: new Float32Array(2), uZoom: 1, uTime: 0 },
  }
}

/** Подгоняет карту под кадр. closeFrom — со скольких пикселей экрана на тексель карта переходит в сетку местности. */
export function fitTexelGrid(grid: TexelGrid, frame: Frame, closeFrom = 1) {
  const { gl, camera, view } = frame
  const density = gl.drawingBufferWidth / frame.width
  const close = camera.zoom * density >= TEXELS_PER_TILE * closeFrom
  const perTile = close ? TEXELS_PER_TILE : camera.zoom * density
  grid.close = close
  grid.perTile = perTile
  grid.pixel = 1 / (camera.zoom * density)
  // Чётный размер: середина карты попадает на границу пикселей, то есть на узел сетки местности.
  grid.width = close ? 2 * (Math.ceil((frame.width / 2 / camera.zoom) * perTile) + 1) : gl.drawingBufferWidth
  grid.height = close ? 2 * (Math.ceil((frame.height / 2 / camera.zoom) * perTile) + 1) : gl.drawingBufferHeight
  grid.centerX = close ? Math.round(camera.x * perTile) / perTile : camera.x
  grid.centerY = close ? Math.round(camera.y * perTile) / perTile : camera.y
  const mapView = grid.view
  mapView.uScreenSize[0] = grid.width
  mapView.uScreenSize[1] = grid.height
  mapView.uScale[0] = (perTile * 2) / grid.width
  mapView.uScale[1] = (perTile * 2) / grid.height
  mapView.uOffset[0] = camera.x - grid.centerX
  mapView.uOffset[1] = camera.y - grid.centerY
  mapView.uZoom = perTile
  mapView.uTime = view.uTime
}

/** Переносит карту на экран: ближайший пиксель карты, без сглаживания. extra — GLSL, правящий finalColor по tile. */
export const blitFragment = (extra = '', uniforms = '') => `#version 300 es
precision highp float;
in vec2 vUV;
uniform sampler2D uTexture;
uniform vec2 uSpan;   // сколько тайлов помещается на экране
uniform vec2 uScale;  // как карта переводит тайлы в свои координаты
uniform vec2 uOffset; // камера минус середина карты, в тайлах
${uniforms}
out vec4 finalColor;

void main() {
  // tile — тайлы от камеры; uOffset переводит их в тайлы от середины карты.
  vec2 tile = (vUV - 0.5) * uSpan;
  vec2 mapTile = tile + uOffset;
  vec2 uv = vec2(mapTile.x * uScale.x, -mapTile.y * uScale.y) * 0.5 + 0.5;
  ivec2 size = textureSize(uTexture, 0);
  finalColor = texelFetch(uTexture, clamp(ivec2(floor(uv * vec2(size))), ivec2(0), size - 1), 0);
  ${extra}
}
`

export function createBlitProgram(gl: WebGL2RenderingContext, extra?: string, uniforms?: string) {
  return createProgram(gl, SCREEN_VERTEX, blitFragment(extra, uniforms))
}

const span = new Float32Array(2)

/** Рисует карту на текущую цель (обычно экран) программой из createBlitProgram. */
export function blitTexelGrid(gl: WebGL2RenderingContext, program: Program, grid: TexelGrid, frame: Frame, texture: Texture, uniforms = {}) {
  span[0] = frame.width / frame.camera.zoom
  span[1] = frame.height / frame.camera.zoom
  program.use({ uTexture: texture, uSpan: span, uScale: grid.view.uScale, uOffset: grid.view.uOffset }, uniforms)
  drawQuad(gl)
}
