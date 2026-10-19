// Страница сверки кадров: рисует сцены из scenes.ts настоящим клиентом TS (проходы игры без изменений) и отдаёт PNG кадра. Открывает её
// dotnet/tools/golden/frames.ts в headless Chrome со SwiftShader; сама по себе в игру не встроена и packages/client не правит.
import '@kharos/client/style.css'
import { loadLang } from '@kharos/i18n'
import { DEFAULT_SETTINGS } from '@kharos/shared/settings'
import { Blast, Shot, Site, createSim, grantResearch } from '@kharos/sim-ts'
import { addCredits } from '@kharos/sim-ts/economy'
import { offerAlliance } from '@kharos/sim-ts/allies'
import { placeBuilding, type BuildingType } from '@kharos/sim-ts/buildings'
import { spawnUnit, type UnitType } from '@kharos/sim-ts/units'
import { drawArt, type Art } from '@kharos/client/game/art'
import { Camera } from '@kharos/client/game/camera'
import { createScene, finish, simOptions, stagePasses } from '@kharos/client/game/game'
import { readHud } from '@kharos/client/game/hud'
import { createMinimap } from '@kharos/client/game/minimap'
import { createGuiPass } from '@kharos/client/gui/pass'
import { createHud } from '@kharos/client/gui/hud'
import { createGui } from '@kharos/client/gui/ui'
import { installSkin } from '@kharos/client/ui/skin'
import { minZoom, type LandWindow } from '@kharos/client/map/landWindow'
import { createRenderer } from '@kharos/client/render/renderer'
import { scenes, type SceneSpec } from './scenes'

/** Детерминированный Math.random (mulberry32): клубы дыма, пыль и зерно следов в каждом запуске одни и те же. */
function seedRandom(seed: number) {
  let state = seed >>> 0
  Math.random = () => {
    state = (state + 0x6d2b79f5) >>> 0
    let t = state
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

let art: Art | null = null

/** Строит мир сцены по её описанию (порядок — в шапке scenes.ts). */
function build(spec: SceneSpec) {
  const settings = { ...DEFAULT_SETTINGS, generator: { ...DEFAULT_SETTINGS.generator, seed: spec.seed }, world: { size: spec.size } }
  const sim = createSim({ ...simOptions(settings), fog: spec.fog })
  for (const { player, amount } of spec.credits) addCredits(sim, player, amount)
  for (const player of spec.research) grantResearch(sim, player)
  const ids: number[] = spec.entities.map((entity) =>
    entity.kind === 'building'
      ? placeBuilding(sim.world, entity.type as BuildingType, entity.x, entity.y, entity.owner)
      : spawnUnit(sim, entity.type as UnitType, entity.owner, entity.x, entity.y),
  )
  for (const [a, b] of spec.allies) {
    offerAlliance(sim, a, b)
    offerAlliance(sim, b, a)
  }
  const resolve = (value: unknown, key: string): unknown => (['units', 'builders'].includes(key) ? (value as number[]).map((i) => ids[i]) : ['building', 'site'].includes(key) && typeof value === 'number' ? ids[value] : value)
  for (const { player, command } of spec.commands) {
    sim.send(player, Object.fromEntries(Object.entries(command).map(([key, value]) => [key, resolve(value, key)])) as never)
  }
  // Каждый тик — ровно один шаг цикла: дробного остатка, из-за которого кадр поплыл бы между запусками, не остаётся.
  for (let i = 0; i < spec.advanceTicks; i++) sim.advance(sim.time.step)
  return { sim, ids, settings }
}

interface Rendered {
  name: string
  png: string
  tick: number
  entities: number
  blasts: number
  shots: number
  sites: number
  errors: string[]
}

async function renderScene(spec: SceneSpec): Promise<Rendered> {
  seedRandom(0x6b686172)
  const errors: string[] = []
  const { sim, ids, settings } = build(spec)
  const camera = new Camera()
  camera.x = spec.camera.x
  camera.y = spec.camera.y
  camera.zoom = spec.camera.zoom
  camera.width = spec.width
  camera.height = spec.height
  camera.minZoom = minZoom(spec.width, spec.height)
  const scene = createScene(sim, spec.player, settings, camera)
  scene.weather = spec.weather
  for (const index of spec.selected) scene.selection.add(ids[index] as never)

  const canvas = document.createElement('canvas')
  Object.assign(canvas.style, { display: 'block', width: `${spec.width}px`, height: `${spec.height}px` })
  document.body.replaceChildren(canvas)

  // Интерфейс — как у приложения: холст gui кладётся в кадр последним проходом.
  let overlay: { surface: HTMLCanvasElement; takeChanged(): boolean } | undefined
  let drawGui = () => {}
  if (spec.hud) {
    const gui = createGui(canvas)
    overlay = { surface: gui.surface, takeChanged: () => true }
    const minimap = createMinimap(scene)
    const game = new Proxy({ scene, minimap, hud: () => readHud(scene) } as Record<string, unknown>, {
      get: (target, key: string) => (key in target ? target[key] : () => {}),
    })
    const hud = createHud(() => game as never, gui)
    drawGui = () => {
      gui.frame(spec.width, spec.height, 1, (ui) => hud.draw(ui, readHud(scene)))
    }
  }

  let landWindow!: LandWindow
  const renderer = createRenderer(canvas, (gl) => {
    const built = finish(stagePasses(gl, scene, art!, false))
    landWindow = built.landWindow
    return overlay ? [...built.passes, createGuiPass(gl, overlay)] : built.passes
  }, (error) => errors.push(String(error)))
  const { width, height } = renderer.resize()
  if (width !== spec.width || height !== spec.height) errors.push(`холст ${width}x${height}, нужен ${spec.width}x${spec.height}`)
  if (canvas.width !== spec.width || canvas.height !== spec.height) errors.push(`буфер ${canvas.width}x${canvas.height}`)

  if (spec.hud) {
    // Шрифты интерфейса догружаются по первому тексту: кадр строится дважды, второй — уже с ними.
    drawGui()
    await document.fonts.ready
    await new Promise((resolve) => setTimeout(resolve, 200))
    drawGui()
  }
  landWindow.update(sim.land, camera, width, height)
  renderer.draw(camera, spec.time, 0)
  // Чтение сразу после рисования, в той же задаче: буфер ещё не отдан странице.
  const png = canvas.toDataURL('image/png')
  const world = sim.world
  const count = (component: unknown) => [...world.query(component as never)].length
  const result: Rendered = {
    name: spec.name,
    png,
    tick: sim.time.tick,
    entities: ids.length,
    blasts: count(Blast),
    shots: count(Shot),
    sites: count(Site),
    errors,
  }
  renderer.destroy()
  landWindow.destroy()
  sim.destroy()
  return result
}

declare global {
  interface Window {
    frames: { names: string[]; spec(name: string): SceneSpec; render(name: string): Promise<Rendered> }
    framesReady: boolean
    framesError?: string
  }
}

async function start() {
  installSkin()
  await loadLang('ru' as never)
  art = drawArt()
  const all = scenes()
  window.frames = {
    names: all.map((s) => s.name),
    spec: (name) => all.find((s) => s.name === name)!,
    render: (name) => renderScene(all.find((s) => s.name === name)!),
  }
  window.framesReady = true
}

start().catch((error) => {
  window.framesError = error instanceof Error ? (error.stack ?? error.message) : String(error)
  window.framesReady = true
})
