import { expect, test } from 'bun:test'
import { outlineOf } from '@kharos/client/game/cursorPass'

/** Та же граница перебором каждой точки по всем кругам. */
function slow(zone: number[]) {
  const dots: number[] = []
  for (let i = 0; i < zone.length; i += 3) {
    const dashes = Math.round((120 * zone[i + 2]) / 12)
    dashes: for (let dash = 0; dash < dashes; dash++) {
      const angle = (dash / dashes) * Math.PI * 2
      const x = zone[i] + Math.cos(angle) * zone[i + 2]
      const y = zone[i + 1] + Math.sin(angle) * zone[i + 2]
      for (let other = 0; other < zone.length; other += 3) {
        if (other !== i && Math.hypot(x - zone[other], y - zone[other + 1]) < zone[other + 2] - 0.01) continue dashes
      }
      dots.push(x, y)
    }
  }
  return dots
}

test('граница зоны по сетке — та же, что перебором, и на большой базе считается быстро', () => {
  let seed = 7
  const random = () => (seed = (seed * 16807) % 2147483647) / 2147483647
  // База на тысячу зданий и труб: главное здание и россыпь кругов поменьше, в том числе по обе стороны нуля.
  const zone = [1.5, 1.5, 12]
  for (let i = 0; i < 1000; i++) zone.push(Math.floor(random() * 120) - 40 + 0.5, Math.floor(random() * 120) - 40 + 0.5, random() < 0.5 ? 5 : 2)
  const started = performance.now()
  const dots = outlineOf(zone)
  const took = performance.now() - started
  const startedSlow = performance.now()
  const expected = slow(zone)
  const tookSlow = performance.now() - startedSlow
  expect(dots).toEqual(expected)
  expect(dots.length).toBeGreaterThan(0)
  // Порог — относительно перебора, а не в миллисекундах: машины сборки бывают медленными.
  expect(took).toBeLessThan(tookSlow / 2)
})
