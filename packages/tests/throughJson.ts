import type { SimSave } from '@kharos/sim-ts'

/**
 * Сохранение, пропущенное через JSON, — проверка, что данные переживают диск и сеть. Карта мира двоичная (в файле
 * она — свой раздел, см. packages/protocol/src/save.ts), её не трогаем.
 */
export function throughJson<T extends SimSave | { sim: SimSave }>(save: T): T {
  const sim: SimSave = 'sim' in save ? save.sim : save
  const copy = JSON.parse(JSON.stringify(save, (_key, value) => (value === sim.land ? undefined : value)))
  if ('sim' in copy) copy.sim.land = sim.land.slice()
  else copy.land = sim.land.slice()
  return copy
}
