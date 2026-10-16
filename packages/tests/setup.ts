import { mock } from 'bun:test'
import { loadLang } from '@kharos/i18n'

// Словарь догружается, а тесты читают строки сразу: исходный язык — до первого теста.
await loadLang('ru')

// KHAROS_SIM=rust bun test — те же тесты против Rust-симуляции (docs/boundary.md): createSim подменяется обёрткой,
// которая сверяет Rust с TS после каждого advance. Сборка wasm — bun run build:wasm. Сверка дорогая, и большим боям мало
// обычных пяти секунд на тест: KHAROS_SIM=rust bun test --timeout 60000.
if (process.env.KHAROS_SIM === 'rust') {
  const { createRustSim, loadSimWasm } = await import('@kharos/sim-rust')
  const wasm = loadSimWasm(await Bun.file(`${import.meta.dir}/../../target/wasm32-unknown-unknown/release/kharos_sim_wasm.wasm`).arrayBuffer())
  const sim = await import('@kharos/sim-ts/sim')
  const index = await import('@kharos/sim-ts')
  // Настоящая createSim — значением, до подмены: обёртка строит на ней свою sim-ts.
  const create = sim.createSim
  const createSim: typeof create = (source) => createRustSim(wasm, source, { check: true, create })
  mock.module('@kharos/sim-ts/sim', () => ({ ...sim, createSim }))
  mock.module('@kharos/sim-ts', () => ({ ...index, createSim }))
}
