import { expect, test } from 'bun:test'
import units from '@kharos/base/units.json'
import buildings from '@kharos/base/buildings.json'
import weapons from '@kharos/base/weapons.json'
import turrets from '@kharos/base/turrets.json'
import resources from '@kharos/base/resources.json'
import ores from '@kharos/base/ores.json'
import products from '@kharos/base/products.json'
import research from '@kharos/base/research.json'
import deposits from '@kharos/base/deposits.json'
import rewards from '@kharos/base/rewards.json'
import { PIPE_REACH } from '@kharos/sim-ts/buildings'
import { stockedFor, type Product } from '@kharos/sim-ts/resources'

/**
 * Данные мода base (packages/base/data) — без проверки типов TS: JSON её не даёт. Здесь — то, что она давала: у каждой
 * записи только известные поля нужного вида, ссылки называют существующие записи, а записи, которые раньше собирали
 * шаблоны (переработка, цех, хранилище, турель с патронами), держат их правила.
 */

type Field =
  | 'number'
  | 'boolean'
  | 'string'
  | { oneOf: readonly string[] }
  | { list: Field }
  | { record: readonly string[]; of: Field }
  | { shape: Record<string, Field>; optional?: readonly string[] }

const names = (table: object) => Object.keys(table)
const GOODS = [...names(resources), ...names(products), ...names(ores)]
const WARES = [...names(resources), ...names(products)]
const ARMOR = ['infantry', 'vehicle', 'heavy', 'air', 'building']
const amounts = (of: readonly string[] = GOODS): Field => ({ record: of, of: 'number' })
const beam: Field = { shape: { radius: 'number', rate: 'number', give: 'boolean', take: 'boolean' }, optional: ['give', 'take'] }
const mounts: Field = { list: { shape: { turret: { oneOf: names(turrets) }, along: 'number', across: 'number' } } }

/** Поля записей каждой таблицы: обязательные — без «?». */
const SCHEMA: Record<string, [object, Record<string, Field>]> = {
  units: [units, {
    speed: 'number', turn: 'number', radius: 'number', cost: 'number', buildTime: 'number', kind: { oneOf: ARMOR.slice(0, 4) }, hp: 'number', supply: 'number',
    'weapon?': { oneOf: names(weapons) }, 'repair?': 'number', 'mounts?': mounts, 'materials?': amounts(WARES), 'requires?': { list: { oneOf: names(buildings) } },
    'research?': { oneOf: names(research) }, 'inventory?': 'number', 'accepts?': { list: { oneOf: GOODS } }, 'hauls?': 'boolean', 'beam?': beam, 'sight?': 'number',
    'ammo?': 'number', 'supplies?': { shape: { radius: 'number', rate: 'number' } },
  }],
  buildings: [buildings, {
    width: 'number', height: 'number', cost: 'number', 'zone?': 'number', 'expand?': 'number', 'defense?': 'boolean', 'hp?': 'number', 'mounts?': mounts, 'sight?': 'number',
    'gate?': 'boolean', 'scan?': 'number', 'power?': 'number', 'extract?': 'boolean', 'materials?': amounts(WARES), 'inventory?': 'number', 'accepts?': { list: { oneOf: GOODS } },
    'limits?': amounts(), 'stores?': 'boolean', 'refines?': { oneOf: names(ores) }, 'assembles?': { oneOf: names(products) }, 'ammo?': 'boolean', 'beam?': beam, 'depot?': 'boolean',
    'rearms?': 'boolean', 'requester?': 'boolean', 'trades?': 'boolean', 'produces?': { list: { oneOf: names(units) } }, 'income?': 'number', 'crowding?': 'boolean',
    'repair?': 'number', 'pipe?': { oneOf: ['surface', 'well'] }, 'isolated?': 'boolean', 'researches?': 'boolean',
    'shield?': { shape: { radius: 'number', capacity: 'number', regen: 'number' } }, 'battery?': { shape: { capacity: 'number', rate: 'number' } },
    'burns?': { shape: { good: { oneOf: names(products) }, seconds: 'number' } }, 'meltdown?': { shape: { radius: 'number', damage: 'number' } },
    'warhead?': { shape: { cost: 'number', materials: amounts(WARES), seconds: 'number', launch: 'number', speed: 'number', blast: { shape: { radius: 'number', core: 'number', damage: 'number' } } } },
    'intercept?': { shape: { radius: 'number', cost: 'number', materials: amounts(WARES), seconds: 'number', speed: 'number', capacity: 'number' } },
    'requires?': { list: { oneOf: names(buildings) } }, 'research?': { oneOf: names(research) }, 'menu?': 'number',
  }],
  weapons: [weapons, {
    shot: { oneOf: ['bullet', 'shell', 'rocket', 'laser', 'arc', 'flame'] }, range: 'number', damage: 'number', reload: 'number', air: 'boolean', 'speed?': 'number',
    'splash?': 'number', 'chain?': 'number', 'minRange?': 'number', 'lob?': 'boolean', 'onlyAir?': 'boolean', 'vs?': { record: ARMOR, of: 'number' }, 'ammo?': 'number',
  }],
  turrets: [turrets, { turn: 'number', radius: 'number', 'weapon?': { oneOf: names(weapons) }, 'repair?': 'number' }],
  resources: [resources, { price: 'number', 'mined?': 'boolean', 'research?': { oneOf: names(research) } }],
  ores: [ores, { resource: { oneOf: names(resources) } }],
  products: [products, { recipe: amounts(names(resources)), yield: 'number' }],
  research: [research, {
    'after?': { list: { oneOf: names(research) } }, 'uses?': amounts(),
    'trigger?': { oneOf: ['lab', 'deposit', 'metal', 'silicon', 'fuel', 'kharite', 'lithium', 'uranium', 'refined'] },
  }],
  deposits: [deposits, { min: 'number', max: 'number', rate: 'number', biomes: { record: ['erg', 'saltFlats', 'redWastes', 'marsh'], of: 'number' } }],
  rewards: [rewards, { credits: 'number' }],
}

/** Что не так со значением: пустой список — всё так. */
function problems(value: unknown, field: Field, at: string): string[] {
  if (typeof field === 'string') return typeof value === field && (field !== 'number' || Number.isFinite(value)) ? [] : [`${at}: не ${field}`]
  if ('oneOf' in field) return field.oneOf.includes(value as string) ? [] : [`${at}: ${JSON.stringify(value)} — нет такого`]
  if ('list' in field) return Array.isArray(value) ? value.flatMap((item, i) => problems(item, field.list, `${at}[${i}]`)) : [`${at}: не список`]
  if (!value || typeof value !== 'object' || Array.isArray(value)) return [`${at}: не объект`]
  if ('record' in field) return Object.entries(value).flatMap(([key, item]) => [...(field.record.includes(key) ? [] : [`${at}.${key}: нет такого`]), ...problems(item, field.of, `${at}.${key}`)])
  return fields(value as Record<string, unknown>, field.shape, at, field.optional)
}

function fields(record: Record<string, unknown>, shape: Record<string, Field>, at: string, optional: readonly string[] = []): string[] {
  const out: string[] = []
  const known = new Map(Object.entries(shape).map(([key, field]) => [key.replace(/\?$/, ''), { field, optional: key.endsWith('?') || optional.includes(key) }]))
  for (const [key, value] of Object.entries(record)) {
    if (key === 'about') {
      if (typeof value !== 'string') out.push(`${at}.about: не строка`)
      continue
    }
    const spec = known.get(key)
    if (!spec) out.push(`${at}.${key}: неизвестное поле`)
    else out.push(...problems(value, spec.field, `${at}.${key}`))
  }
  for (const [key, spec] of known) if (!spec.optional && !(key in record)) out.push(`${at}.${key}: нет поля`)
  return out
}

test('у записей данных только известные поля нужного вида, а ссылки называют существующие записи', () => {
  const out: string[] = []
  for (const [name, [table, shape]] of Object.entries(SCHEMA)) for (const [key, record] of Object.entries(table)) out.push(...fields(record, shape, `${name}.${key}`))
  expect(out).toEqual([])
})

test('переработка, цеха, хранилища и турели с патронами держат правила своего рода', () => {
  const ORE_RESOURCE = ores as Record<string, { resource: string }>
  for (const [type, spec] of Object.entries(buildings) as [string, Record<string, any>][]) {
    if (spec.refines) {
      // Склад переработки — два места по 30: под руду и под готовое.
      const resource = ORE_RESOURCE[spec.refines].resource
      expect([type, spec.accepts, spec.limits, spec.inventory]).toEqual([type, [spec.refines, resource], { [spec.refines]: 30, [resource]: 30 }, 60])
    }
    if (spec.assembles) {
      // Цех держит сырьё на четыре сборки и 30 готового.
      const limits = { ...stockedFor(spec.assembles as Product), [spec.assembles]: 30 }
      expect([type, spec.limits, spec.accepts, spec.inventory]).toEqual([type, limits, Object.keys(limits), Object.values(limits).reduce((sum, amount) => sum + amount!, 0)])
    }
    if (spec.stores) expect([type, spec.accepts.length]).toEqual([type, 1])
    if (spec.pipe) expect([type, spec.expand]).toEqual([type, PIPE_REACH])
    if (spec.ammo) expect([type, spec.inventory, spec.accepts]).toEqual([type, 120, ['ammo']])
  }
})

test('места в меню строителя не повторяются; у каждого ресурса — своя руда', () => {
  const menu = Object.values(buildings).flatMap((spec) => ('menu' in spec ? [spec.menu] : []))
  expect(new Set(menu).size).toBe(menu.length)
  expect(Object.values(ores).map(({ resource }) => resource).sort()).toEqual(names(resources).sort())
})

test('доли месторождений в каждом биоме дают в сумме единицу', () => {
  for (const biome of ['erg', 'saltFlats', 'redWastes', 'marsh'] as const)
    expect(Object.values(deposits).reduce((sum, { biomes }) => sum + biomes[biome], 0)).toBeCloseTo(1, 9)
})
