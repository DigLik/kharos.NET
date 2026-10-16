import type { Entity } from '@kharos/ecs'
import type { ViewBox } from '../net/protocol'
import type { EditOp } from '../sim/editOps'
import { createAudio } from '../audio/audio'
import { createLandWindow, minZoom, type LandWindow } from '../map/landWindow'
import type { MapSettings } from '../map/settings'
import { createTerrainPass } from '../map/terrainPass'
import { graphics } from '../render/graphics'
import { createRenderer, type Pass } from '../render/renderer'
import type { Session } from '../net/connect'
import { Building, Player, Position, Unit, isOwn, weatherAt, type BuildingType, type Command, type Sim, type SimOptions, type UnitType } from '../sim'
import { createLightingPass } from '../weather/lightingPass'
import { createPrecipitationPass } from '../weather/precipitationPass'
import { createBuildingsPass } from './buildings/buildingsPass'
import { createCombatPasses } from './combatPass'
import { Camera } from './camera'
import { CameraMotion } from './cameraMotion'
import { createControls } from './controls'
import { createCursorPass } from './cursorPass'
import { createDecalsPass } from './decalsPass'
import { createFogPass } from './fogPass'
import { createAlerts } from './alerts'
import { createMachines } from './machines'
import { createMotors } from './motors'
import { createWorldSounds } from './worldSounds'
import { createInterfaceSounds } from './interfaceSounds'
import { createShake } from './shake'
import { createSoundscape } from './soundscape'
import { createDepositsPass } from './depositsPass'
import { createPavingPass } from './pavingPass'
import { createDropsPass } from './dropsPass'
import { createFlowPass } from './flowPass'
import { startFrames } from './frames'
import { createGuiPass, type Overlay } from '../gui/pass'
import { readHud, type HudState } from './hud'
import { createMinimap, type Minimap } from './minimap'
import type { CarryPick, PaveTool, Scene } from './scene'
import { createPowerPass } from './powerPass'
import { createSelectionPass } from './selectionPass'
import { loadCamera, storeCamera } from './storage'
import { createUnitsPasses } from './units/unitsPass'
import type { Art } from './art'
import { loadedArt } from './loadArt'

/** Как часто вкладка запоминает место камеры, в секундах. */
const CAMERA_INTERVAL = 10
/** Витрина: масштаб в пикселях на тайл, как быстро камера догоняет действие и как медленно кружит вокруг него. */
const SHOWCASE_ZOOM = 28
const SHOWCASE_FOLLOW = 0.4
const SHOWCASE_ORBIT = 0.05
const SHOWCASE_RADIUS = 6
/** С какой ширины экрана меню стоит слева от действия и сколько пикселей оно занимает. */
const SHOWCASE_WIDE = 760
const SHOWCASE_MENU = 400

/**
 * Как играть на холсте. slot — локальная игра из этого слота сохранений: камера встаёт туда, где была, и место
 * запоминается в нём же. showcase — витрина для фона меню: ни управления, ни звука, ни тумана, а камера сама
 * следит за происходящим.
 */
/**
 * Что игра показывает: обычно копию мира хоста (Session), а витрина меню — симуляцию, которую считает сама
 * вкладка; у неё нет поколений мира — она не начинается заново из-под игры.
 */
/**
 * Как часто, в секундах, вкладка сообщает хосту, где её камера: хост чаще шлёт то, что на экране, а редактор
 * показывает камеры игроков.
 */
const VIEW_GAP = 0.15

export type GameSession = Omit<Session, 'sim'> & { sim: Sim & { readonly generation?: number; readonly kept?: boolean; respawn?(): void; say?(text: string): void; edit?(edit: EditOp): void; view?(box: ViewBox): void } }

export interface GameOptions {
  slot?: string
  showcase?: boolean
  /** Редактор сохранений: мир стоит, и уведомлений о врагах и атаках нет — в нём враги ставятся руками. */
  editor?: boolean
  /**
   * Интерфейс на холсте (src/gui): игра кладёт его в кадр последним проходом, поверх мира и курсора, а нажатия, что
   * пришлись на его панели (covers), миру не отдаёт. Строит его оболочка приложения (src/gui/app.ts).
   */
  overlay?: Overlay & {
    covers(x: number, y: number): boolean
    /** Сколько пикселей CSS с краёв экрана закрыто панелями: камера ставит середину видимого между ними. */
    insets(width: number, height: number): { top: number; bottom: number; right: number }
  }
}

export interface Game {
  readonly scene: Scene
  /** Применяет настройки панели. Смена параметров генератора или мира начинает мир заново. */
  /** Начинает мир заново с теми же настройками. */
  restart(): void
  /** Состояние интерфейса игрока на этот момент. */
  hud(): HudState
  /** Посылает симуляции команду от имени игрока. */
  send(command: Command): void
  /** Начинает выбор места под здание; null — отменяет его. */
  place(building: BuildingType | null): void
  /** Начинает укладку или снятие покрытия; null — отменяет. */
  pave(tool: PaveTool | null): void
  /** Курсор встал на кнопку здания в меню стройки или ушёл с неё: пока он там, на карте видны границы зон. */
  zones(shown: boolean): void
  /** Начинает набор маршрута выбранным грузовикам щелчками по зданиям; false — отменяет. */
  route(start: boolean): void
  /** Начинает выбор зданий, которые будут обслуживать выбранные грузовики; false — отменяет. */
  serve(start: boolean): void
  /**
   * Ручной приказ выбранным грузовикам: 'source' — выбрать щелчком, откуда взять; груз со склада — выбрать, куда его
   * отвезти; null — отменить. См. Scene.carrying.
   */
  carry(pick: CarryPick): void
  /** Открывает окно приказа выбранным грузовикам по зданию; null — закрывает. */
  truckMenu(building: number | null): void
  /** Проигравший начинает заново: на сервере — новый стартовый набор в новом месте, в локальной игре — новый мир. */
  respawn(): void
  /** Написать в чат сетевой игры. */
  say(text: string): void
  /** Правка редактора живого мира: уходит хосту, см. /editor. */
  edit(edit: EditOp): void
  /** Начинает выбор точки патруля выбранным бойцам; false — отменяет. */
  patrol(start: boolean): void
  /** Начинает наводку ядерного удара готовой боеголовкой шахты; null — отменяет. */
  aim(silo: number | null): void
  /** Начинает выбор места, где MCV развернётся; null — отменяет. */
  deploy(mcv: number | null): void
  /** Мини-карта нижней панели. */
  readonly minimap: Minimap
  /** Ставит центр экрана в точку карты, в тайлах. */
  lookAt(x: number, y: number): void
  /** Камера летит к точке карты, в тайлах. */
  flyTo(x: number, y: number): void
  /** Оставляет в выделении только юнитов вида type; remove — убирает их. */
  narrow(type: UnitType, remove: boolean): void
  /** Наводит камеру на выделенное. */
  lookAtSelection(): void
  /** Выбирает группу цифры digit; add — добавляет к выделенным. Повторно подряд — ещё и камера на неё. */
  selectGroup(digit: string, add: boolean): void
  /** Запоминает своих выделенных группой digit; никого не выбрано — распускает её. */
  assignGroup(digit: string): void
  /** Очищает группу digit. */
  clearGroup(digit: string): void
  /** Выходит из режима наведения, ничего не отдавая: «Отмена» полосы режима. */
  cancelMode(): void
  /** Завершает режим наведения: набранный маршрут и обслуживание отдаются грузовикам. «Готово» полосы режима. */
  finishMode(): void
  /** Посылает выделенных юнитов в точку карты, в тайлах. */
  moveSelected(x: number, y: number): void
  /** Выключен ли звук; выбор хранится в браузере. */
  muted: boolean
  /** Поверх игры открыто меню: управление выключено, звук тише. Сама игра идёт дальше. */
  paused: boolean
  /** Зовётся, когда Escape нечего отменять: интерфейс открывает меню игры. */
  set onMenu(value: () => void)
  /** Перечитывает настройки звука: их поменяли в меню. */
  refreshSound(): void
  /** Останавливает игру и освобождает ресурсы. */
  destroy(): void
}

export const simOptions = (settings: MapSettings): SimOptions => ({ generator: settings.generator, size: settings.world.size, rules: settings.rules })

/** Состояние клиента для мира sim: ничего не выбрано, никаких режимов. */
export function createScene(sim: Sim, player: number, settings: MapSettings, camera = new Camera()): Scene {
  return {
    sim,
    player,
    camera,
    settings,
    selection: new Set(),
    groups: new Map(),
    selectionBox: null,
    placing: null,
    paving: null,
    paveFrom: null,
    routing: null,
    serving: null,
    carrying: null,
    truckMenu: null,
    patrolling: false,
    aiming: null,
    deploying: null,
    grid: false,
    zonesHeld: false,
    zonesHover: false,
    weather: weatherAt(sim.options, sim.time.elapsed),
  }
}

/**
 * Создаёт проходы игры по одному, отдавая управление после каждого: проход собирает свои шейдеры и заливает свой
 * атлас, и экран загрузки растягивает это на кадры (см. warmUp.ts). Игре, которой нужны все проходы сразу, — finish.
 * Порядок проходов — порядок отрисовки, снизу вверх. Витрине не нужны зоны, выделение и курсор.
 */
export function* stagePasses(gl: WebGL2RenderingContext, scene: Scene, art: Art, showcase: boolean): Generator<void, { passes: Pass[]; landWindow: LandWindow }> {
  const landWindow = createLandWindow(gl)
  const terrain = createTerrainPass(gl, scene, landWindow)
  yield
  const deposits = createDepositsPass(gl, scene, art.deposits)
  yield
  const paving = createPavingPass(gl, scene, art.paving)
  yield
  const drops = createDropsPass(gl, scene, art.drops)
  yield
  const decals = createDecalsPass(gl, scene, art.decals)
  yield
  const units = createUnitsPasses(gl, scene, art.units)
  yield
  const buildings = createBuildingsPass(gl, scene, art.buildings)
  yield
  const flow = createFlowPass(gl, scene)
  yield
  const combat = createCombatPasses(gl, scene, art.combat)
  yield
  const precipitation = createPrecipitationPass(gl, scene, landWindow)
  yield
  const lighting = createLightingPass(gl, scene, [units.ground, buildings, units.emplacements])
  yield
  const fog = createFogPass(gl, scene)
  yield
  let play: { power: Pass; selection: Pass; cursor: Pass } | null = null
  if (!showcase) {
    const power = createPowerPass(gl, scene, art.power)
    yield
    const selection = createSelectionPass(gl, scene, art.selection)
    yield
    play = { power, selection, cursor: createCursorPass(gl, scene) }
    yield
  }
  const passes = [
    terrain,
    deposits,
    paving,
    drops,
    // Следы, гарь и остовы — на земле, под юнитами.
    decals,
    units.ground,
    buildings,
    // Поток по трубам — поверх труб, под турелями и летающими.
    flow,
    units.emplacements,
    // Пыль от винтов — на земле и на крышах, под летающими.
    combat.dust,
    // Летающие — над зданиями.
    units.air,
    precipitation,
    combat.lights,
    lighting,
    // Туман — над миром и его светом, под зонами, выделением и курсором. Он же — темнота за краем мира: её видно
    // и в витрине, где тумана нет.
    fog,
    ...(play ? [play.power] : []),
    combat.effects,
    ...(play ? [play.selection, play.cursor] : []),
  ]
  return { passes, landWindow }
}

/** Доводит пошаговую работу до конца сразу, без передышек. */
export function finish<T>(steps: Generator<void, T>): T {
  let step = steps.next()
  while (!step.done) step = steps.next()
  return step.value
}

/**
 * Собирает игру на холсте: отрисовку и управление поверх копии мира из session — и запускает кадры.
 * Сам мир считает хост: воркер локальной игры или сервер. Если запустить не удалось (нет WebGL 2, не собрался
 * шейдер), бросает ошибку. onError получает ошибки, случившиеся уже во время игры; игра после них остановлена.
 */
export function createGame(
  canvas: HTMLCanvasElement,
  settings: MapSettings,
  onError: (error: unknown) => void,
  session: GameSession,
  { slot, showcase = false, editor = false, overlay }: GameOptions = {},
): Game {
  const camera = new Camera()
  // Камера возвращается туда, где была, только вместе с миром: в новом мире слота места ещё нет.
  const view = slot ? loadCamera(slot) : null
  if (view) {
    camera.x = view.x
    camera.y = view.y
    camera.zoomTo(view.zoom)
  }
  const scene = createScene(session.sim, session.player, settings, camera)

  // Всё, что живёт на видеокарте, создаётся здесь: после потери контекста рендер вызовет это заново. Атласы и
  // шейдеры уже на видеокарте — их по частям готовит экран загрузки (см. warmUp.ts), — здесь только то, что у игры своё.
  const art = loadedArt()
  let landWindow: LandWindow
  const renderer = createRenderer(
    canvas,
    (gl) => {
      const built = finish(stagePasses(gl, scene, art, showcase))
      landWindow = built.landWindow
      return overlay ? [...built.passes, createGuiPass(gl, overlay)] : built.passes
    },
    onError,
  )
  /** Какие края холста закрыты интерфейсом: его панели лежат поверх холста. Меряется каждый кадр — панели меняются. */
  const measureInset = () => {
    if (!overlay) return
    const insets = overlay.insets(canvas.clientWidth, canvas.clientHeight)
    camera.inset.top = insets.top
    camera.inset.bottom = insets.bottom
    camera.inset.right = insets.right
  }
  const motion = new CameraMotion(camera)
  motion.simple = editor
  const controls = showcase ? null : createControls(canvas, scene, motion, overlay ? (x, y) => overlay.covers(x, y) : undefined)
  // В редакторе мир стоит, и гул машин и зданий ни к чему: звука нет вовсе.
  const audio = showcase || editor ? null : createAudio()
  const shake = createShake()
  const soundscape = audio && createSoundscape(scene, audio, shake)
  if (!showcase && !editor) scene.alerts = createAlerts(scene, audio)
  const interfaceSounds = audio ? createInterfaceSounds(session.sim, audio) : null
  const machines = audio && createMachines(scene, audio)
  const motors = audio && createMotors(scene, audio)
  const worldSounds = audio && createWorldSounds(scene, audio)

  const saveCamera = () => slot && storeCamera(slot, camera)
  let sinceSave = 0
  // Игрок на сервере появляется не в начале мира, а база тестовой карты — у ближайшего месторождения:
  // камера встаёт на его юнит, как только мир готов. Так же — когда хост начал мир заново.
  let centered = view !== null
  let generation = session.sim.generation

  /**
   * Камера витрины: плавно идёт к середине всех юнитов и зданий, медленно кружа вокруг неё, — так в кадре бой или
   * работающая база, даже когда всё стоит.
   */
  let orbit = Math.random() * Math.PI * 2
  const direct = (seconds: number) => {
    let x = 0
    let y = 0
    let count = 0
    // Здания — тоже действие: у стройки и обороны оно вокруг них.
    for (const component of [Unit, Building]) {
      for (const [, position] of scene.sim.world.query(Position, component)) {
        x += position.x
        y += position.y
        count++
      }
    }
    if (!count) return
    // Слева на широком экране — меню: действие держится правее него.
    camera.inset.left = camera.width >= SHOWCASE_WIDE ? Math.min(camera.width * 0.4, SHOWCASE_MENU) : 0
    orbit += seconds * SHOWCASE_ORBIT * Math.PI * 2
    const target = { x: x / count + Math.cos(orbit) * SHOWCASE_RADIUS, y: y / count + Math.sin(orbit) * SHOWCASE_RADIUS }
    const focus = camera.focus
    // Первый кадр — сразу на месте, дальше — вдогонку.
    const follow = centered ? 1 - Math.exp(-seconds * SHOWCASE_FOLLOW) : 1
    centered = true
    camera.centerOn(focus.x + (target.x - focus.x) * follow, focus.y + (target.y - focus.y) * follow)
    camera.zoomTo(SHOWCASE_ZOOM)
  }

  // Мир один на все вкладки, и начинает его заново хост; на сервере — не может никто.
  const restart = () => session.local?.restart(simOptions(scene.settings), scene.settings.battle)

  /**
   * Какую видимую часть хост знает: камера шлётся, когда сдвинулась хотя бы на тайл, не чаще раза в VIEW_GAP с —
   * а прыгнула (мини-карта, группа, сигнал) — сразу: хост чаще шлёт то, что на экране, см. очередь рассылки в host.ts.
   */
  let viewSent = ''
  let viewBox = { left: 0, top: 0, right: 0, bottom: 0 }
  let sinceView = 0
  const stop = startFrames((seconds, hidden) => {
    const { sim } = scene
    sim.advance(seconds)
    sinceView += seconds
    if (session.sim.view) {
      const { from, to } = scene.camera.visible
      const box = { left: Math.round(from.x), top: Math.round(from.y), right: Math.round(to.x), bottom: Math.round(to.y) }
      // Прыжок — экран уехал больше чем на половину себя.
      const jumped = Math.abs(box.left - viewBox.left) > (box.right - box.left) / 2 || Math.abs(box.top - viewBox.top) > (box.bottom - box.top) / 2
      const key = JSON.stringify(box)
      if ((jumped || sinceView >= VIEW_GAP) && key !== viewSent) {
        sinceView = 0
        viewSent = key
        viewBox = box
        session.sim.view(box)
      }
    }
    if (session.sim.generation !== generation) {
      generation = session.sim.generation
      scene.selection.clear()
      scene.groups.clear()
      scene.placing = null
      scene.deploying = null
      scene.paving = null
      scene.paveFrom = null
      scene.routing = null
      scene.serving = null
      scene.patrolling = false
      scene.aiming = null
      // Тот же мир заново — вход в редактор и выход из него: камера остаётся, где была.
      centered = !!session.sim.kept
    }
    controls?.update(seconds)
    if (showcase) direct(seconds)
    else if (!centered) {
      // Камера, которую игроку задали в редакторе, главнее места его юнитов.
      for (const [, data] of sim.world.query(Player)) {
        if (data.id !== scene.player || data.camera.length !== 3) continue
        motion.jump(data.camera[0], data.camera[1])
        camera.zoomTo(data.camera[2])
        centered = true
      }
      if (!centered) for (const [entity, position] of sim.world.query(Position, Unit)) {
        if (!isOwn(sim, scene.player, entity)) continue
        motion.jump(position.x, position.y)
        centered = true
        break
      }
    }

    const { width, height } = renderer.resize()
    camera.width = width
    camera.height = height
    measureInset()
    camera.minZoom = minZoom(width, height)
    if (camera.zoom < camera.minZoom) camera.zoomTo(camera.minZoom)
    // Середина видимой части не уходит за границы карты: край карты можно подвести к краю панели, но не под неё.
    const focus = camera.focus
    camera.centerOn(
      Math.min(sim.bounds.right, Math.max(sim.bounds.left, focus.x)),
      Math.min(sim.bounds.bottom, Math.max(sim.bounds.top, focus.y)),
    )
    motion.measure(seconds)

    soundscape?.update(seconds, motion.speed)
    // В редакторе тревоги ни к чему: администратор видит весь мир, и «замечен враг» сыпалось бы без конца.
    if (!(session.sim as { editor?: boolean }).editor) scene.alerts?.update(seconds)
    machines?.update(seconds)
    motors?.update(seconds)
    worldSounds?.update(seconds)
    interfaceSounds?.update(seconds, scene.selection)
    shake.update(seconds)

    sinceSave += seconds
    if (sinceSave >= CAMERA_INTERVAL) {
      sinceSave = 0
      saveCamera()
    }

    // Пока контекст потерян, рисовать некуда, а скрытую вкладку не видно; игра при этом продолжает идти и звучать.
    if (renderer.lost || hidden) return
    // Тряска уводит только картинку, вместе с землёй: управление и сохранение видят камеру на месте.
    const { x: shakeX, y: shakeY } = shake.offset(camera.zoom)
    camera.x += shakeX
    camera.y += shakeY
    landWindow.update(sim.land, camera, width, height)
    const { time } = sim
    scene.weather = weatherAt(sim.options, time.elapsed + time.alpha * time.step)
    renderer.draw(camera, time.elapsed + time.alpha * time.step, seconds)
    camera.x -= shakeX
    camera.y -= shakeY
  }, onError, () => graphics().fps, audio !== null)

  // Последний шанс сохраниться: вкладку закрывают или уводят в фон.
  window.addEventListener('pagehide', saveCamera)

  const api: Game = {
    scene,
    restart,
    hud: () => readHud(scene),
    send: (command) => scene.sim.send(scene.player, command),
    place(building) {
      scene.placing = building
      if (building) scene.paving = scene.deploying = null
    },
    say(text) {
      session.sim.say?.(text)
    },
    edit(edit) {
      session.sim.edit?.(edit)
    },
    respawn() {
      if (session.local) restart()
      else session.sim.respawn?.()
    },
    patrol(start) {
      scene.patrolling = start
      if (start) scene.placing = scene.paving = scene.routing = scene.serving = scene.aiming = scene.deploying = null
    },
    aim(silo) {
      scene.aiming = silo as Entity | null
      if (silo !== null) {
        scene.placing = scene.paving = scene.routing = scene.serving = scene.deploying = null
        scene.patrolling = false
      }
    },
    deploy(mcv) {
      scene.deploying = mcv as Entity | null
      if (mcv !== null) {
        scene.placing = scene.paving = scene.routing = scene.serving = scene.aiming = null
        scene.patrolling = false
      }
    },
    route(start) {
      scene.routing = start ? [] : null
      if (start) scene.placing = scene.paving = scene.serving = scene.deploying = scene.carrying = null
    },
    serve(start) {
      scene.serving = start ? [] : null
      if (start) scene.placing = scene.paving = scene.routing = scene.deploying = scene.carrying = null
    },
    carry(pick) {
      scene.carrying = pick
      scene.truckMenu = null
      if (pick) scene.placing = scene.paving = scene.routing = scene.serving = scene.deploying = null
    },
    truckMenu(building) {
      scene.truckMenu = building as Entity | null
    },
    pave(tool) {
      scene.paving = tool
      scene.paveFrom = null
      if (tool) scene.placing = scene.deploying = null
    },
    zones(shown) {
      scene.zonesHover = shown
    },
    minimap: createMinimap(scene),
    lookAt: (x, y) => motion.jump(x, y),
    flyTo: (x, y) => motion.flyTo(x, y),
    lookAtSelection: () => controls?.lookAtSelection(),
    selectGroup: (digit, add) => controls?.selectGroup(digit, add),
    assignGroup: (digit) => controls?.assignGroup(digit),
    clearGroup: (digit) => void scene.groups.delete(digit),
    cancelMode: () => controls?.cancelMode(),
    finishMode: () => controls?.finishMode(),
    narrow: (type, remove) => controls?.narrow(type, remove),
    moveSelected(x, y) {
      const units = [...scene.selection].filter((entity) => scene.sim.world.has(entity, Unit))
      if (units.length) scene.sim.send(scene.player, { type: 'move', units, x: Math.floor(x), y: Math.floor(y) })
    },
    get muted() {
      return audio?.muted ?? true
    },
    set muted(value) {
      if (audio) audio.muted = value
    },
    set onMenu(value: () => void) {
      if (controls) controls.onMenu = value
    },
    get paused() {
      return controls?.paused ?? false
    },
    set paused(value) {
      if (controls) controls.paused = value
      if (audio) audio.dimmed = value
    },
    refreshSound: () => audio?.refresh(),
    destroy() {
      stop()
      window.removeEventListener('pagehide', saveCamera)
      saveCamera()
      controls?.destroy()
      interfaceSounds?.destroy()
      motors?.destroy()
      audio?.destroy()
      scene.sim.destroy()
      renderer.destroy()
      landWindow.destroy()
    },
  }
  return api
}
