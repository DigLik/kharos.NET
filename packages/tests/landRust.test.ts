import { expect, test } from 'bun:test'
import { existsSync } from 'node:fs'
import { DEFAULT_CONFIG, areaOf, createLand, getChunk, type GeneratorConfig } from '@kharos/shared/terrain'
import { loadSimWasm, rustLand } from '@kharos/sim-rust'

/** Сборка sim.wasm: bun run build:wasm. Без неё сверку не с чем делать. */
const WASM = `${import.meta.dir}/../../target/wasm32-unknown-unknown/release/kharos_sim_wasm.wasm`

/** Сколько байт земли Rust и TS расходятся на карте; первое расхождение — в сообщении. */
function compare(config: GeneratorConfig, half: number) {
  const wasm = loadSimWasm(require('node:fs').readFileSync(WASM))
  const bounds = { left: -half, top: -half, right: half, bottom: half }
  const rust = rustLand(wasm, config, bounds)
  const area = areaOf(bounds)
  const land = createLand(config, area)
  let at = 0
  let differ = 0
  let first = ''
  for (let y = 0; y < area.height; y++) {
    for (let x = 0; x < area.width; x++) {
      const chunk = getChunk(land, area.left + x, area.top + y)
      for (let i = 0; i < chunk.length; i++, at++) {
        if (chunk[i] === rust[at]) continue
        if (!differ) first = `чанк ${area.left + x},${area.top + y}, тайл ${i >> 2}, байт ${i & 3}: TS ${chunk[i]}, Rust ${rust[at]}`
        differ++
      }
    }
  }
  expect(rust.length).toBe(at)
  return { differ, first }
}

test.skipIf(!existsSync(WASM))('генератор земли на Rust даёт те же байты, что TS, — с горами и выводимым', () => {
  for (const config of [
    DEFAULT_CONFIG,
    { ...DEFAULT_CONFIG, seed: 3 },
    { ...DEFAULT_CONFIG, seed: 98765, peakChance: 0.9, cliffShare: 0.8, tripleChance: 0.6 },
    { ...DEFAULT_CONFIG, seed: 42, zoneScale: 30, biomeScale: 90 },
    { ...DEFAULT_CONFIG, blank: true },
  ]) {
    const { differ, first } = compare(config, 256)
    expect(`${config.seed}: ${differ} ${first}`).toBe(`${config.seed}: 0 `)
  }
})
