import { expect, test } from 'bun:test'
import type { Entity } from '../src/ecs'
import { DEFAULT_SETTINGS } from '../src/map/settings'
import {
  BUILDINGS, Inventory, Off, RESEARCH, Research, Upgrade, canPlace, createSim, creditsOf, hasResearch, isUnlocked, researchOf, stockOf, topicLack, upgradeLack,
  type BuildingType, type Sim,
} from '../src/sim'
import { STORES, placeBuilding, storeFor } from '../src/sim/buildings'
import type { Amounts, Ware } from '../src/sim/resources'
import { addCredits } from '../src/sim/economy'
import { throughJson } from './throughJson'

const options = { generator: DEFAULT_SETTINGS.generator, size: 1024 }
const TICK = 1 / 20
const seconds = (sim: Sim, time: number) => {
  for (let i = 0; i < Math.round(time / TICK); i++) sim.advance(TICK)
}

/** База игрока 1 в ряд на скале в одной сети труб: главное здание, электростанция, здания из списка и все хранилища. */
function base(extra: BuildingType[]) {
  const sim = createSim(options)
  addCredits(sim, 1, 10000)
  const layout: [BuildingType, number][] = [['command', 0], ['generator', 4]]
  let at = 7
  for (const type of [...extra, ...STORES]) {
    layout.push([type, at])
    at += BUILDINGS[type].width + 1
  }
  for (let y = -100; y < 100; y++) {
    for (let x = -100; x < 100; x++) {
      if (!layout.every(([type, dx]) => canPlace(sim, type, x + dx, y))) continue
      let piped = true
      for (let dx = 0; dx < at && piped; dx++) piped = canPlace(sim, 'pipe', x + dx, y - 1)
      if (!piped) continue
      const buildings = layout.map(([type, dx]) => placeBuilding(sim.world, type, x + dx, y, 1))
      for (let dx = 0; dx < at; dx++) placeBuilding(sim.world, 'pipe', x + dx, y - 1, 1)
      const shelves = buildings.slice(2 + extra.length)
      const stash = (items: Amounts) => {
        for (const [item, amount] of Object.entries(items) as [Ware, number][]) sim.world.get(shelves[STORES.indexOf(storeFor(item)!)], Inventory)!.items[item] = amount
      }
      return { sim, plant: buildings[1], buildings: buildings.slice(2, 2 + extra.length), stash }
    }
  }
  throw new Error('Не нашлось места под базу')
}

const study = (sim: Sim, lab: Entity, topic: keyof typeof RESEARCH) => {
  sim.send(1, { type: 'research', lab, topic })
  sim.advance(TICK)
}

test('техцентр исследует тему за кредиты, компоненты и время; открытое остаётся у игрока и переживает сохранение', () => {
  const { sim, buildings: [lab], stash } = base(['techCenter'])
  expect(topicLack(sim, 1, lab, 'armor')).toBe('materials')
  study(sim, lab, 'armor')
  expect(sim.world.has(lab, Research)).toBe(false)

  stash({ parts: 10 })
  const credits = creditsOf(sim, 1)
  study(sim, lab, 'armor')
  expect(sim.world.get(lab, Research)!.topic).toBe('armor')
  expect(credits - creditsOf(sim, 1)).toBe(RESEARCH.armor.cost)
  expect(stockOf(sim, 1).items.parts).toBe(10 - RESEARCH.armor.parts)
  // Техцентр занят: вторая тема не начнётся, а ту же не начать и в другом.
  expect(topicLack(sim, 1, lab, 'armor')).toBe('running')
  study(sim, lab, 'beams')
  expect(sim.world.get(lab, Research)!.topic).toBe('armor')

  seconds(sim, RESEARCH.armor.seconds - 1)
  expect(hasResearch(sim, 1, 'armor')).toBe(false)
  seconds(sim, 1.5)
  expect(researchOf(sim, 1)).toEqual(['armor'])
  expect(sim.world.has(lab, Research)).toBe(false)
  expect(topicLack(sim, 1, lab, 'armor')).toBe('done')
  // Чужому игроку тема не открыта.
  expect(hasResearch(sim, 2, 'armor')).toBe(false)

  const copy = createSim(throughJson(sim.save()))
  expect(researchOf(copy, 1)).toEqual(['armor'])
  // Снесли техцентр — открытое остаётся.
  sim.world.destroy(lab)
  expect(hasResearch(sim, 1, 'armor')).toBe(true)
})

test('тема по цепочке ждёт предыдущую; отмена возвращает цену; без энергии исследование стоит', () => {
  const { sim, plant, buildings: [lab], stash } = base(['techCenter'])
  stash({ parts: 20 })
  expect(topicLack(sim, 1, lab, 'siege')).toBe('after')
  study(sim, lab, 'siege')
  expect(sim.world.has(lab, Research)).toBe(false)

  const credits = creditsOf(sim, 1)
  study(sim, lab, 'beams')
  seconds(sim, 5)
  const progress = sim.world.get(lab, Research)!.progress
  expect(progress).toBeGreaterThan(0)
  // Выключенный техцентр не исследует.
  sim.world.add(lab, Off)
  seconds(sim, 5)
  expect(sim.world.get(lab, Research)!.progress).toBe(progress)
  sim.world.remove(lab, Off)
  // Без электростанции энергии нет вовсе.
  sim.world.destroy(plant)
  seconds(sim, 5)
  expect(sim.world.get(lab, Research)!.progress).toBe(progress)

  sim.send(1, { type: 'cancelResearch', lab })
  sim.advance(TICK)
  expect(sim.world.has(lab, Research)).toBe(false)
  expect(creditsOf(sim, 1)).toBeGreaterThanOrEqual(credits)
  expect(stockOf(sim, 1).items.parts).toBe(20)
  expect(researchOf(sim, 1)).toEqual([])
})

test('исследования открывают лучевые турели и уровни зданий', () => {
  const { sim, plant, buildings: [lab], stash } = base(['techCenter'])
  stash({ parts: 20, blocks: 40 })
  expect(isUnlocked(sim, 1, 'laserTurret')).toBe(false)
  expect(upgradeLack(sim, 1, plant)).toBe('research')
  sim.send(1, { type: 'upgrade', building: plant, builders: [] })
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(false)

  study(sim, lab, 'beams')
  seconds(sim, RESEARCH.beams.seconds + 1)
  expect(isUnlocked(sim, 1, 'laserTurret')).toBe(true)
  expect(isUnlocked(sim, 1, 'arcTurret')).toBe(true)

  study(sim, lab, 'levels2')
  seconds(sim, RESEARCH.levels2.seconds + 1)
  expect(upgradeLack(sim, 1, plant)).toBeUndefined()
  sim.send(1, { type: 'upgrade', building: plant, builders: [] })
  sim.advance(TICK)
  expect(sim.world.has(plant, Upgrade)).toBe(true)
})
