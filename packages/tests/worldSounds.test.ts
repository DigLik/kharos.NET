import { expect, test } from 'bun:test'
import type { Audio } from '@kharos/client/audio/audio'
import type { LoopName, SoundName } from '@kharos/client/audio/sounds'
import { Camera } from '@kharos/client/game/camera'
import type { Scene } from '@kharos/client/game/scene'
import { createWorldSounds } from '@kharos/client/game/worldSounds'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Building, Glare, Off, Owner, Position, Shield, Site, Trade, createSim } from '@kharos/sim-ts'
import { Gate } from '@kharos/sim-ts/components'
import { placeBuilding } from '@kharos/sim-ts/buildings'

/** Звуки мира без браузера: камера в (0, 0), звук записывает, что ему велели. */
function setup() {
  const sim = createSim({ generator: DEFAULT_SETTINGS.generator, size: 256 })
  const camera = new Camera()
  camera.width = 1280
  camera.height = 800
  camera.zoomTo(32)
  camera.centerOn(0, 0)
  const played: SoundName[] = []
  const loops = new Map<LoopName, number>()
  const audio = { play: (name: SoundName) => played.push(name), loop: (name: LoopName, volume: number) => loops.set(name, volume) } as unknown as Audio
  const sounds = createWorldSounds({ sim, player: 1, camera } as unknown as Scene, audio)
  // Мир сначала устаканивается: всё, что есть в первые секунды, — не события.
  const update = (seconds = 0.1) => sounds.update(seconds)
  for (let i = 0; i < 30; i++) update()
  return { sim, played, loops, update }
}

test('стройка: стучит, пока движется, и звенит, достроившись', () => {
  const { sim, played, loops, update } = setup()
  const entity = placeBuilding(sim.world, 'generator', 2, 2, 1)
  sim.world.add(entity, Site({ type: 'generator', progress: 0, demolish: false }))
  update()
  sim.world.get(entity, Site)!.progress = 5
  update()
  expect(loops.get('construct')).toBeGreaterThan(0)
  sim.world.remove(entity, Site)
  update()
  expect(played).toContain('built')
  for (let i = 0; i < 10; i++) update()
  expect(loops.get('construct')).toBe(0)
})

test('ворота лязгают, щит звенит от удара, гаснет и поднимается', () => {
  const { sim, played, update } = setup()
  const gate = sim.world.spawn(Position({ x: 1, y: 1 }), Building({ type: 'wall', phase: 0, level: 1 }), Gate({ open: true }))
  const shield = placeBuilding(sim.world, 'shieldGenerator', 4, 4, 1)
  sim.world.get(shield, Shield)!.charge = 100
  update()
  sim.world.get(gate, Gate)!.open = false
  sim.world.get(shield, Shield)!.hit = 5
  update()
  expect(played).toEqual(['shieldHit', 'gate'])
  sim.world.get(shield, Shield)!.charge = 0
  update()
  sim.world.get(shield, Shield)!.charge = 50
  update()
  expect(played).toEqual(['shieldHit', 'gate', 'shieldDown', 'shieldUp'])
})

test('ядерный взрыв слышен, вспыхнув', () => {
  const { sim, played, update } = setup()
  sim.world.spawn(Position({ x: 3, y: 3 }), Glare({ radius: 12, until: 1000 }))
  update()
  expect(played).toEqual(['nukeBlast'])
})

test('рубильник, корабль с покупкой, подарок', () => {
  const { sim, played, update } = setup()
  const port = placeBuilding(sim.world, 'spaceport', 3, 3, 1)
  const factory = placeBuilding(sim.world, 'generator', -3, 3, 2)
  update()
  sim.world.add(port, Off())
  sim.world.add(port, Trade({ resource: 'metal', wanted: 10, left: 10, total: 10, buy: true }))
  update()
  sim.world.remove(port, Trade)
  sim.world.get(factory, Owner)!.player = 1
  update()
  expect(played).toEqual(['powerOff', 'shipLand', 'delivered', 'gift'])
})
