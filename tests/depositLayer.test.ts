import { expect, test } from 'bun:test'
import { DEFAULT_CONFIG, Terrain, setTile } from '@kharos/shared/terrain'
import { createHost } from '../src/net/host'
import { decodeServer } from '@kharos/protocol'
import { createReplica, type Replica } from '../src/net/replica'
import { DEPOSIT_CELL, Deposit, Position, createSim, depositsIn, type Sim } from '@kharos/sim-ts'
import { addDeposit, fillDeposits } from '@kharos/sim-ts/deposits'
import { paint, putDeposit } from '@kharos/sim-ts/editor'
import { editTile } from '@kharos/sim-ts/landMemory'
import { throughJson } from './throughJson'

const world = () => createSim({ generator: DEFAULT_CONFIG, size: 256 })

/** Клетка, где генератор ничего не положил, а вся она — песок: туда кисть нарисует скалу. */
function emptyCell(sim: Sim) {
  for (let cellY = -3; cellY < 3; cellY++) for (let cellX = -3; cellX < 3; cellX++) if (!depositsIn(sim, cellX, cellY).length) return { cellX, cellY }
  throw new Error('нет пустой клетки')
}

test('кисть и правка карты месторождений не создают и не двигают', () => {
  const sim = world()
  const { cellX, cellY } = emptyCell(sim)
  const x = cellX * DEPOSIT_CELL
  const y = cellY * DEPOSIT_CELL
  // Вся клетка — ровная скала: генератор положил бы сюда месторождение, но клетка уже взята пустой.
  for (let i = 0; i < DEPOSIT_CELL; i += 9) for (let j = 0; j < DEPOSIT_CELL; j += 9) paint(sim, x + i + 4, y + j + 4, 9, { terrain: Terrain.Rock, tier: 1, cliff: false })
  expect(depositsIn(sim, cellX, cellY)).toEqual([])

  // Правка игрой — так же: клетка берётся у генератора до правки.
  const other = world()
  const before = depositsIn(other, 1, 1).map((spot) => ({ ...spot }))
  for (const spot of before) editTile(other, spot.x, spot.y, { terrain: Terrain.Sand, biome: 0, tier: 0, cliff: false })
  expect(depositsIn(other, 1, 1)).toEqual(before)
})

test('слой месторождений — в сохранении: и правки, и клетки, взятые до правок карты', () => {
  const sim = world()
  const { cellX, cellY } = emptyCell(sim)
  paint(sim, cellX * DEPOSIT_CELL + 20, cellY * DEPOSIT_CELL + 20, 15, { terrain: Terrain.Rock, tier: 1, cliff: false })
  expect(putDeposit(sim, cellX * DEPOSIT_CELL + 20, cellY * DEPOSIT_CELL + 20, 'kharite', 77)).toBe(true)
  const again = createSim(throughJson(sim.save()))
  expect(depositsIn(again, cellX, cellY)).toEqual([{ x: cellX * DEPOSIT_CELL + 20, y: cellY * DEPOSIT_CELL + 20, kind: 'kharite', reserve: 77 }])
})

/** Хост на sim и подключённый к нему клиент; mirrored — сколько месторождений у клиента. */
function joined(sim: Sim) {
  const host = createHost(sim, 1)
  let replica: Replica | undefined
  const peer = host.join((data) => {
    const message = decodeServer(data)
    if (message.type === 'welcome' && !replica) replica = createReplica(message, (reply) => peer.receive(reply), () => peer.leave())
    else replica!.receive(message)
  })
  return { host, mirrored: () => [...replica!.deposits.cells.values()].flat().length }
}

test('клиент получает месторождения от хоста и правки — следом', () => {
  const sim = createSim({ generator: DEFAULT_CONFIG, size: 256, fog: false })
  const { host, mirrored } = joined(sim)
  const spots = () => [...sim.deposits.cells.values()].flat().length
  expect(mirrored()).toBe(spots())
  expect(mirrored()).toBeGreaterThan(0)
  const at = depositsIn(sim, 0, 0)[0] ?? depositsIn(sim, 1, 0)[0]
  for (let dy = 0; dy < 2; dy++) for (let dx = 0; dx < 2; dx++) setTile(sim.land, at.x + 3 + dx, at.y + dy, { terrain: Terrain.Rock, biome: 0, tier: 1, cliff: false })
  putDeposit(sim, at.x + 3, at.y, 'fuel', 10)
  host.advance(1 / 20)
  expect(mirrored()).toBe(spots())
})

test('месторождения в тумане клиенту не шлются', () => {
  const sim = world()
  const { host, mirrored } = joined(sim)
  host.advance(1)
  expect([...sim.deposits.cells.values()].flat().length).toBeGreaterThan(0)
  expect(mirrored()).toBe(0)
})

test('правки месторождений из прежних сохранений переносятся в слой', () => {
  const sim = world()
  fillDeposits(sim)
  const [spot] = depositsIn(sim, 0, 0).length ? depositsIn(sim, 0, 0) : depositsIn(sim, 1, 1)
  const save = sim.save()
  // Так выглядели правки до слоя: в сущности Deposit, без самого слоя.
  delete save.deposits
  save.world.entities.push([9999, { Position: { x: spot.x, y: spot.y }, Deposit: { mined: 5, kind: 'kharite', reserve: 300, gone: false } }] as never)
  const loaded = createSim(save)
  expect(depositsIn(loaded, Math.floor(spot.x / DEPOSIT_CELL), Math.floor(spot.y / DEPOSIT_CELL))[0]).toMatchObject({ kind: 'kharite', reserve: 300 })
  // Массив строки обхода один на весь обход: берём первую строку и выходим — for..of сам закроет обход.
  const [, , deposit] = (() => {
    for (const row of loaded.world.query(Position, Deposit)) return row
    throw new Error('Месторождения из сохранения нет')
  })()
  expect(deposit).toEqual({ mined: 5, spent: false })
})

test('клиенту шлются ушедшие месторождения, которые игрок помнит, и не шлются новые, которых он не видел', () => {
  const sim = world()
  const { host, mirrored } = joined(sim)
  host.advance(1)
  expect(mirrored()).toBe(0)
  sim.deposits.ghosts.push({ x: 0, y: 0, kind: 'metal', reserve: 100, players: [1] }, { x: 4, y: 0, kind: 'fuel', reserve: 100, players: [2] })
  sim.deposits.revision++
  host.advance(1)
  expect(mirrored()).toBe(1)
  // Вся карта разведана: шлются все месторождения генератора и помнимое ушедшее, а новое, которого никто не нашёл, — нет.
  sim.vision.explore(1, [0, 256 * 256])
  addDeposit(sim, { x: 8, y: 0, kind: 'silicon', reserve: 100, found: [] })
  host.advance(1)
  expect(sim.vision.exploredIn(1, 8, 0, 2, 2)).toBe(true)
  const generated = [...sim.deposits.cells.values()].flat().filter((spot) => !spot.found).length
  expect(mirrored()).toBe(generated + 1)
})
