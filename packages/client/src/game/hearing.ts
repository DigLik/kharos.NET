import type { Scene } from './scene'

/** Издалека слышно тише: при таком масштабе (пикселей на тайл) громкость полная, при вдвое меньшем — вдвое тише. */
const NEAR_ZOOM = 32

/**
 * Насколько слышно то, что в (x, y): 1 — на экране вблизи, 0 — далеко за краем или с большой высоты. За краем экрана
 * звук стихает на расстоянии reach, в долях полуширины экрана.
 */
export function nearness(scene: Scene, x: number, y: number, reach: number) {
  const { camera } = scene
  const halfWidth = camera.width / 2 / camera.zoom
  const halfHeight = camera.height / 2 / camera.zoom
  const outside = Math.max(0, Math.abs(x - camera.x) - halfWidth, Math.abs(y - camera.y) - halfHeight)
  return Math.max(0, 1 - outside / (halfWidth * reach)) * Math.min(1, camera.zoom / NEAR_ZOOM)
}

/** В какой стороне (x, y): -1 — у левого края экрана, 1 — у правого, дальше — за ними. */
export function sideOf(scene: Scene, x: number) {
  const { camera } = scene
  return (x - camera.x) / (camera.width / 2 / camera.zoom)
}
