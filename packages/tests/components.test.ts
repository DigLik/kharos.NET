import { expect, test } from 'bun:test'
import { SAVED } from '@kharos/sim-ts/components'
import defaults from '../../crates/sim/src/components.json'

// Rust создаёт компоненты по своей таблице (crates/sim/src/components.rs): поля и их порядок — как у create() в TS,
// иначе снимки миров разошлись бы на первой же новой сущности.
test('компоненты по умолчанию на Rust — те же, что в TS, с полями в том же порядке', () => {
  const table = defaults as Record<string, unknown>
  expect(Object.keys(table)).toEqual(SAVED.map((component) => component.key))
  for (const component of SAVED) expect([component.key, JSON.stringify(table[component.key])]).toEqual([component.key, JSON.stringify(component.create())])
})
