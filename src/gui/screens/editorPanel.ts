// Панель редактора на холсте — режим бога над миром: кистью правится карта, ставятся и сносятся здания и юниты любого
// игрока, у выбранного — владелец, прочность и склад, у игрока — кредиты. Каждая правка — EditOp: так одна и та же
// панель правит и сохранение, и живой мир на сервере. Пока панель открыта, левая и правая кнопки на холсте — её.
// Перенесено со страницы (src/ui/EditorPanel.tsx): та же логика, отрисовка — вызовами ui.

import type { Entity } from '@kharos/ecs'
import type { Game } from '../../game/game'
import { ordersAt } from '../../game/orders'
import { placementOf } from '../../game/placing'
import type { SceneEdit } from '../../game/scene'
import { hasText, hostText, t, textOf, type Key } from '@kharos/i18n'
import { Biome, Dunes as DUNES_OF, MAX_PEAK_RADIUS, Terrain, type Dunes } from '@kharos/shared/terrain'
import {
  Armed,
  BUILDING_TYPES,
  Builds,
  Building,
  Converting,
  GOODS,
  Hauler,
  Health,
  Inventory,
  Orders,
  Owner,
  Path,
  Producer,
  Site,
  Tactics,
  UNIT_TYPES,
  Unit,
  creditsOf,
  type BuildingType,
  type Command,
  type Sim,
  type UnitType,
} from '@kharos/sim-ts'
import { DEPOSIT_KINDS, DEPOSIT_SIZE, DEPOSIT_TYPES, depositAt, reserveLeft, type DepositKind } from '@kharos/sim-ts/deposits'
import {
  canPutDeposit,
  depositUnder,
  depositsInBox,
  describeTile,
  entitiesIn,
  entityAt,
  erase,
  moveGhost,
  playerCamera,
  playersOf,
  turretFacings,
  type Brush,
  type BrushShape,
} from '@kharos/sim-ts/editor'
import type { EditOp, EditResult } from '@kharos/sim-ts/editOps'
import { stanceSlots } from '../../ui/commands'
import { chips, menuButton, numberField, slider } from '../form'
import type { Ui } from '../ui'
import { COLORS } from '../widgets'

type Tool = 'select' | 'paint' | 'building' | 'unit' | 'deposit'
const TOOLS: Tool[] = ['select', 'paint', 'building', 'unit', 'deposit']
/** Варианты кисти: значение и ключ его названия в словаре. */
const TERRAINS: [Terrain | undefined, Key][] = [
  [undefined, 'common.asIs'],
  [Terrain.Rock, 'editor.terrains.rock'],
  [Terrain.Sand, 'editor.terrains.sand'],
  [Terrain.Swamp, 'editor.terrains.swamp'],
  [Terrain.Mountain, 'editor.terrains.mountain'],
]
const DUNES: [Dunes | undefined, Key][] = [
  [undefined, 'common.asIs'],
  [DUNES_OF.Natural, 'editor.dunes.natural'],
  [DUNES_OF.None, 'editor.dunes.none'],
  [DUNES_OF.Many, 'editor.dunes.many'],
]
const BIOMES: [Biome | undefined, Key][] = [
  [undefined, 'common.asIs'],
  [Biome.Erg, 'editor.biomes.erg'],
  [Biome.SaltFlats, 'editor.biomes.saltFlats'],
  [Biome.RedWastes, 'editor.biomes.redWastes'],
  [Biome.Marsh, 'editor.biomes.marsh'],
]
const SHAPES: [BrushShape, Key][] = [
  ['square', 'editor.shapes.square'],
  ['circle', 'editor.shapes.circle'],
  ['spray', 'editor.shapes.spray'],
]
const TIERS: (number | undefined)[] = [undefined, 0, 1, 2, 3]
const CLIFFS: [boolean | undefined, Key][] = [
  [undefined, 'common.asIs'],
  [true, 'editor.cliff'],
  [false, 'editor.gentle'],
]
const DEGREE = Math.PI / 180
/** Ширина панели и её содержимого. */
export const EDITOR_WIDTH = 300
const INNER = EDITOR_WIDTH - 24

/** Варианты на текущем языке. */
const named = <T>(options: [T, Key][]): [T, string][] => options.map(([value, key]) => [value, t(key)])

/** Название значения из вариантов кисти; нет такого — прочерк. */
const nameIn = <T>(options: [T | undefined, Key][], value: T) => {
  const key = options.find(([option]) => option === value)?.[1]
  return key ? t(key) : '—'
}

/** Название приказа из очереди; у приказа без названия в словаре — его ключ. */
const orderName = (type: Command['type']) => {
  const key = `editor.orders.${type}`
  return hasText(key) ? t(key) : type
}

/** Что делает юнит или здание сейчас и что у него в очереди: строки для панели. */
function tasksOf(sim: Sim, entity: Entity): string[] {
  const { world } = sim
  const tasks: string[] = []
  const nameOf = (target: number) => {
    const unit = world.get(target as Entity, Unit)?.type
    const building = world.get(target as Entity, Building)?.type ?? world.get(target as Entity, Site)?.type
    return unit ? t(`units.${unit}`) : building ? t(`buildings.${building}`) : `#${target}`
  }
  const converting = world.get(entity, Converting)
  if (converting) tasks.push(world.has(entity, Unit) ? t('editor.task.deploying') : t('editor.task.packing'))
  const producer = world.get(entity, Producer)
  if (producer?.queue.length) tasks.push(t('editor.task.produces', { list: producer.queue.map((type) => t(`units.${type}`)).join(', ') }))
  const builds = world.get(entity, Builds)
  if (builds && world.alive(builds.site as Entity)) tasks.push(t(world.has(builds.site as Entity, Site) ? 'editor.task.builds' : 'editor.task.repairs', { name: nameOf(builds.site) }))
  const armed = world.get(entity, Armed)
  if (armed && armed.target >= 0 && world.alive(armed.target as Entity)) tasks.push(t('editor.task.attacks', { name: nameOf(armed.target) }))
  const hauler = world.get(entity, Hauler)
  if (hauler) {
    if (hauler.mine >= 0) tasks.push(t('editor.task.haulsFrom', { mine: hauler.mine }))
    if (hauler.route.length) tasks.push(t('editor.task.route', { stops: hauler.route.map(nameOf).join(' → ') }))
    if (hauler.supply >= 0) tasks.push(t('editor.task.supplies', { name: nameOf(hauler.supply) }))
    if (hauler.pickup >= 0) tasks.push(t('editor.task.pickup'))
    if (hauler.serve.length) tasks.push(t('editor.task.serves', { list: hauler.serve.map(nameOf).join(', ') }))
    if (hauler.from >= 0) tasks.push(t('editor.task.carries', { good: t(`goods.${hauler.resource}`), from: nameOf(hauler.from), to: hauler.to >= 0 ? nameOf(hauler.to) : '?' }))
  }
  const tactics = world.get(entity, Tactics)
  if (tactics?.patrol.length) tasks.push(t('editor.task.patrol', { points: tactics.patrol.length / 2 }))
  const path = world.get(entity, Path)
  if (path) tasks.push(t('editor.task.goes', { x: path.goalX, y: path.goalY }))
  for (const { command } of world.get(entity, Orders)?.list ?? []) tasks.push(t('editor.task.queued', { what: orderName(command.type) }))
  return tasks
}

export interface EditorPanelOptions {
  /** Мир, по которому панель показывает, что есть: у сетевого редактора — копия мира с хоста. */
  sim: Sim
  game: Game
  /** Применяет правку: локально — сразу и с ответом, в сети — шлёт хосту, и ответ придёт в чат. */
  apply(edit: EditOp): EditResult
  /** Редактор живого мира на сервере: правки доходят через тик, призрака юнита нет, игрок сцены остаётся своим. */
  remote?: boolean
  /** Ники игроков по номеру: на сервере игроков узнают по ним, а не по номерам. */
  names?: () => ReadonlyMap<number, string>
  /** Правка ушла: локальный редактор помечает несохранённое. */
  changed?(): void
}

export interface EditorPanel {
  /** Панель справа: заголовок, подзаголовок и кнопки внизу (выйти, сохранить) — их рисует footer. top — отступ сверху. */
  draw(ui: Ui, title: string, subtitle: string, top: number, footer: () => void): void
  destroy(): void
}

export function createEditorPanel({ sim, game, apply, remote = false, names, changed }: EditorPanelOptions): EditorPanel {
  const label = (id: number) => (names?.().has(id) ? `${id} ${names().get(id)}` : String(id))
  let tool: Tool = 'select'
  let player = playersOf(sim)[0] ?? 1
  let brush: Brush = { terrain: Terrain.Rock, shape: 'square' }
  let size = 3
  let building: BuildingType = 'generator'
  let unit: UnitType = 'tank'
  let status = ''
  let hover: { x: number; y: number } | null = null
  /** Вид и запас месторождения, которое кладёт инструмент «Ресурсы». */
  let kind: DepositKind = 'metal'
  let reserve = DEPOSIT_KINDS.metal.max
  /** Приказ мышью: следующий щелчок по карте — приказ выбранным юнитам. */
  let ordering = false
  /** Призрак юнита под указателем, см. moveGhost. В сетевом редакторе его нет: мир копии правит только хост. */
  let ghost: Entity | undefined
  /** Выбранные месторождения: левые верхние тайлы. */
  let pickedSpots: { x: number; y: number }[] = []
  const { scene } = game

  /** Правка: ответ — в строку состояния; удалась — панель помечает её. */
  const edit = (op: EditOp) => {
    const failed = apply(op)
    if (failed) status = textOf(hostText(failed))
    else {
      status = ''
      changed?.()
    }
    return !failed
  }
  const picked = () => pickedSpots.map((at) => depositAt(sim, at.x, at.y)).filter((spot) => spot !== null)
  const isPicked = (spot: { x: number; y: number }) => pickedSpots.some((at) => at.x === spot.x && at.y === spot.y)
  const spotsOf = (list: readonly { x: number; y: number }[]) => list.map(({ x, y }) => ({ x, y }))
  const clearPicks = () => {
    scene.selection.clear()
    pickedSpots = []
  }
  const eraseSelection = () => {
    const entities = [...scene.selection]
    const spots = picked()
    if (!entities.length && !spots.length) return
    if (entities.length) edit({ op: 'erase', entities })
    if (spots.length) edit({ op: 'removeDeposit', spots: spotsOf(spots) })
    clearPicks()
  }
  /** Где ляжет месторождение под указателем на тайле (x, y): серединой под ним, как здание. */
  const depositSpot = (x: number, y: number) => ({ x: x - Math.floor(DEPOSIT_SIZE / 2), y: y - Math.floor(DEPOSIT_SIZE / 2) })
  /** Убирает призрак юнита: он не часть мира. */
  const dropGhost = () => {
    if (ghost !== undefined) erase(sim, ghost)
    ghost = undefined
  }
  /** Инструмент и игрок — в сцену: от них зависит, что показано под указателем. */
  const sync = () => {
    // Ничьё — не игрок: смотреть его глазами незачем. В сети сцена смотрит глазами своего игрока.
    if (!remote) scene.player = player || playersOf(sim)[0] || 1
    scene.placing = tool === 'building' ? building : null
    scene.selectionBox = null
    if (tool !== 'unit') dropGhost()
  }

  /** Приказ мышью выбранным юнитам: каждому — от имени его владельца, как правой кнопкой в игре. */
  const giveOrders = (point: { x: number; y: number }, queue: boolean) => {
    const byOwner = new Map<number, Entity[]>()
    for (const entity of scene.selection) {
      if (!sim.world.has(entity, Unit)) continue
      const owner = sim.world.get(entity, Owner)?.player ?? 0
      byOwner.set(owner, [...(byOwner.get(owner) ?? []), entity])
    }
    const viewer = scene.player
    const orders: EditOp[] = []
    for (const [owner, units] of byOwner) {
      // Свой и чужой — с точки зрения владельца: по его врагу — атака, по его стройке — помощь.
      scene.player = owner
      for (const command of ordersAt(scene, units, point, queue)) orders.push({ op: 'order', player: owner, command })
    }
    scene.player = viewer
    if (!orders.length) status = t('host.order')
    for (const order of orders) edit(order)
  }

  /** Обработчик кнопок мыши на холсте: что делает инструмент. */
  const createEdit = (): SceneEdit => {
    /** Что тянут мышью: выбранное разом, рамку выделения или рамку камеры игрока. */
    let dragging: { kind: 'group' | 'box' | 'camera' | 'zoom'; from: { x: number; y: number } } | null = null
    /** Рамка камеры выбранного игрока: что видно на экране размером с этот, в тайлах. */
    const cameraBox = () => {
      const view = player ? playerCamera(sim, player) : undefined
      if (!view) return null
      const halfWidth = scene.camera.width / view.zoom / 2
      const halfHeight = scene.camera.height / view.zoom / 2
      return { view, left: view.x - halfWidth, top: view.y - halfHeight, right: view.x + halfWidth, bottom: view.y + halfHeight }
    }
    /** Попала ли точка в рамку камеры: в угол — менять масштаб, в край — двигать. */
    const cameraHit = (point: { x: number; y: number }) => {
      const box = cameraBox()
      if (!box) return null
      const reach = 10 / scene.camera.zoom
      const nearX = Math.min(Math.abs(point.x - box.left), Math.abs(point.x - box.right)) < reach
      const nearY = Math.min(Math.abs(point.y - box.top), Math.abs(point.y - box.bottom)) < reach
      const withinX = point.x > box.left - reach && point.x < box.right + reach
      const withinY = point.y > box.top - reach && point.y < box.bottom + reach
      if (nearX && nearY) return 'zoom'
      if ((nearX && withinY) || (nearY && withinX)) return 'camera'
      return null
    }
    let last = ''
    return {
      press(point, phase, shift) {
        const x = Math.floor(point.x)
        const y = Math.floor(point.y)
        const tile = `${x},${y}`
        if (ordering && phase === 'down') {
          // Приказ мышью: выбранным юнитам, как правой кнопкой в игре. Shift — ещё приказ.
          giveOrders(point, shift)
          if (!shift) ordering = false
          return
        }
        if (tool === 'paint') {
          if (phase === 'up' || (phase === 'drag' && tile === last)) return
          last = tile
          edit({ op: 'paint', x, y, size, brush })
        } else if (tool === 'building' && phase === 'down') {
          // Здание встаёт там, где его показывает рамка под указателем.
          const placement = placementOf(scene)
          if (placement) edit({ op: 'building', type: placement.type, x: placement.x, y: placement.y, player })
        } else if (tool === 'unit' && phase === 'down') {
          // Призрак стоит на месте юнита: убрать его, иначе юнит туда не встанет.
          dropGhost()
          edit({ op: 'unit', type: unit, x, y, player })
        } else if (tool === 'deposit' && phase === 'down') {
          const at = depositSpot(x, y)
          edit({ op: 'deposit', x: at.x, y: at.y, kind, reserve })
        } else if (dragging?.kind === 'camera' || dragging?.kind === 'zoom') {
          // Рамку камеры игрока тянут за край — двигают, за угол — меняют масштаб: угол идёт за мышью.
          const box = cameraBox()
          if (box) {
            if (dragging.kind === 'camera') {
              edit({ op: 'camera', player, camera: { ...box.view, x: box.view.x + point.x - dragging.from.x, y: box.view.y + point.y - dragging.from.y } })
              dragging.from = point
            } else {
              const half = Math.max(Math.abs(point.x - box.view.x) / scene.camera.width, Math.abs(point.y - box.view.y) / scene.camera.height)
              edit({ op: 'camera', player, camera: { ...box.view, zoom: scene.camera.clampZoom(1 / (2 * Math.max(half, 1e-3))) } })
            }
          }
          if (phase === 'up') dragging = null
        } else if (tool === 'select' && phase === 'down' && cameraHit(point)) {
          dragging = { kind: cameraHit(point)!, from: point }
        } else if (tool === 'select') {
          if (phase === 'down') {
            const target = entityAt(sim, point.x, point.y)
            // Мимо юнитов и зданий — может быть, по месторождению: выбирается и тащится с ними наравне.
            const spot = target === undefined ? depositUnder(sim, point.x, point.y) : null
            const chosen = target !== undefined ? scene.selection.has(target) : spot ? isPicked(spot) : false
            if (target === undefined && !spot) {
              if (!shift) clearPicks()
              dragging = { kind: 'box', from: point }
            } else if (shift && chosen) {
              if (target !== undefined) scene.selection.delete(target)
              else pickedSpots = pickedSpots.filter((at) => at.x !== spot!.x || at.y !== spot!.y)
            } else {
              if (!shift && !chosen) clearPicks()
              if (target !== undefined) scene.selection.add(target)
              else if (!chosen) pickedSpots = [...pickedSpots, { x: spot!.x, y: spot!.y }]
              dragging = { kind: 'group', from: { x, y } }
            }
          } else if (dragging?.kind === 'box') {
            const { from } = dragging
            const left = Math.min(from.x, point.x)
            const top = Math.min(from.y, point.y)
            const right = Math.max(from.x, point.x)
            const bottom = Math.max(from.y, point.y)
            const hits = entitiesIn(sim, left, top, right, bottom)
            scene.selectionBox = phase === 'up' ? null : { fromX: from.x, fromY: from.y, toX: point.x, toY: point.y, hits }
            if (phase === 'up') {
              for (const entity of hits) scene.selection.add(entity)
              for (const spot of depositsInBox(sim, left, top, right, bottom)) if (!isPicked(spot)) pickedSpots = [...pickedSpots, { x: spot.x, y: spot.y }]
            }
          } else if (dragging?.kind === 'group' && (x !== dragging.from.x || y !== dragging.from.y)) {
            // Выбранное тянут мышью вместе: сдвиг целыми тайлами, если всем есть где встать.
            const dx = x - dragging.from.x
            const dy = y - dragging.from.y
            if (edit({ op: 'move', entities: [...scene.selection], dx, dy, deposits: [...pickedSpots] })) {
              pickedSpots = pickedSpots.map((at) => ({ x: at.x + dx, y: at.y + dy }))
              dragging.from = { x, y }
            }
          }
          if (phase === 'up') dragging = null
        }
      },
      cancel() {
        dragging = null
        last = ''
        scene.selectionBox = null
      },
      secondary(point) {
        // Правый щелчок сносит: по выбранному — всё выбранное, иначе — то, что под указателем.
        const target = entityAt(sim, point.x, point.y)
        const spot = target === undefined ? depositUnder(sim, point.x, point.y) : null
        if (target === undefined && !spot) return
        if (target !== undefined ? scene.selection.has(target) : isPicked(spot!)) return eraseSelection()
        if (target !== undefined) edit({ op: 'erase', entities: [target] })
        else edit({ op: 'removeDeposit', spots: [{ x: spot!.x, y: spot!.y }] })
      },
      hover(tile) {
        hover = tile
        // Месторождение, которое встанет по щелчку, — призраком под указателем.
        const at = tile && depositSpot(tile.x, tile.y)
        scene.depositGhost = tool === 'deposit' && at ? { ...at, kind, blocked: !canPutDeposit(sim, at.x, at.y) } : null
        // Юнит, который встанет по щелчку, — тоже призраком.
        if (tool === 'unit' && tile && !remote) ghost = moveGhost(sim, ghost, unit, player, tile.x, tile.y)
        else dropGhost()
        // Пометки: выбранные месторождения, кисть карты и камера выбранного игрока — рамка экрана с крестом.
        const marks = picked().map((spot) => ({ fromX: spot.x, fromY: spot.y, toX: spot.x + DEPOSIT_SIZE, toY: spot.y + DEPOSIT_SIZE }))
        const box = cameraBox()
        if (box) {
          const arm = 12 / scene.camera.zoom
          marks.push({ fromX: box.left, fromY: box.top, toX: box.right, toY: box.bottom })
          marks.push({ fromX: box.view.x - arm, fromY: box.view.y, toX: box.view.x + arm, toY: box.view.y })
          marks.push({ fromX: box.view.x, fromY: box.view.y - arm, toX: box.view.x, toY: box.view.y + arm })
        }
        if (tool === 'paint' && tile) {
          const from = Math.floor(size / 2)
          marks.push({ fromX: tile.x - from, fromY: tile.y - from, toX: tile.x - from + size, toY: tile.y - from + size })
        }
        scene.marks = marks
      },
    }
  }

  scene.edit = createEdit()
  sync()

  // Delete и Backspace сносят выбранное, Escape снимает приказ мышью.
  const onKey = (event: KeyboardEvent) => {
    if (event.code === 'Delete' || event.code === 'Backspace') eraseSelection()
    if (event.code === 'Escape') ordering = false
  }
  window.addEventListener('keydown', onKey)

  /** Строка выбора с подписью сверху: варианты в ряд, с переносом. */
  const choice = <T>(ui: Ui, id: string, title: string, options: [T, string][], value: T): { picked: T } | null => {
    ui.text(title, { color: COLORS.dim })
    return chips(ui, id, options, value, INNER, { small: true })
  }

  /** Выбранное: владелец, прочность, поворот, задания, склад. Если выбрано несколько — правится каждое. */
  const inspector = (ui: Ui, entities: Entity[]) => {
    const { world } = sim
    const first = entities[0]
    const type = world.get(first, Unit)?.type
    const buildingType = world.get(first, Building)?.type
    const name = type ? t(`units.${type}`) : buildingType ? t(`buildings.${buildingType}`) : '—'
    const health = world.get(first, Health)
    const inventory = world.get(first, Inventory)
    const owner = world.get(first, Owner)?.player ?? 0
    const players = [0, ...playersOf(sim)]
    const units = entities.filter((entity) => world.has(entity, Unit))
    const facing = units.length ? world.get(units[0], Unit)?.facing : undefined
    // У одного выбранного — ползунок на каждую турель, у нескольких — общий на все.
    const turrets = entities.length === 1 ? turretFacings(sim, first) : []
    const turretAt = entities.map((entity) => turretFacings(sim, entity)[0]).find((angle) => angle !== undefined)
    const stance = world.get(first, Tactics)?.stance
    const tasks = entities.length === 1 ? tasksOf(sim, first) : []
    const angleSlider = (id: string, title: string, angle: number, apply: (radians: number) => void) => {
      ui.text(title, { color: COLORS.dim })
      const degrees = Math.round(angle / DEGREE)
      const next = slider(ui, id, degrees, { min: -180, max: 180, step: 15, width: INNER })
      if (next !== degrees) apply(next * DEGREE)
    }
    ui.box({ direction: 'column', gap: 6 }, () => {
      ui.text(`${name}${entities.length > 1 ? t('editor.andMore', { count: entities.length - 1 }) : ''}`, { color: COLORS.dim })
      const newOwner = choice(ui, 'editor:owner', t('editor.owner'), players.map((id) => [id, label(id)]), owner)
      if (newOwner) edit({ op: 'owner', entities, player: newOwner.picked })
      if (health) {
        const share = Math.round((health.value / health.max) * 100)
        ui.text(t('editor.health', { percent: share }), { color: COLORS.dim })
        const next = slider(ui, 'editor:health', share, { min: 1, max: 100, step: 1, width: INNER })
        if (next !== share) edit({ op: 'health', entities, share: next / 100 })
      }
      if (facing !== undefined) angleSlider('editor:hull', t('editor.hull', { degrees: Math.round(facing / DEGREE) }), facing, (angle) => edit({ op: 'facing', entities, angle }))
      turrets.forEach((angle, index) =>
        angleSlider(`editor:turret:${index}`, turrets.length > 1 ? t('editor.turretOf', { index: index + 1, degrees: Math.round(angle / DEGREE) }) : t('editor.turret', { degrees: Math.round(angle / DEGREE) }), angle, (next) => edit({ op: 'turret', entities, angle: next, index })),
      )
      if (entities.length > 1 && turretAt !== undefined) angleSlider('editor:turrets', t('editor.turrets', { degrees: Math.round(turretAt / DEGREE) }), turretAt, (angle) => edit({ op: 'turret', entities, angle }))
      if (units.length) {
        ui.text(t('editor.tasks'), { color: COLORS.dim })
        if (entities.length === 1) {
          if (tasks.length) for (const task of tasks) ui.text(task, { wrap: INNER })
          else ui.text(t('editor.idle'), { color: COLORS.dim })
        }
        if (stance) {
          const slots = stanceSlots()
          const picked = chips(ui, 'editor:stance', slots.map((slot) => [slot.stance, slot.label] as const), stance, INNER, { small: true, tips: slots.map((slot) => slot.title) })
          if (picked) for (const entity of entities) edit({ op: 'order', player: world.get(entity, Owner)?.player ?? 0, command: { type: 'stance', units: [entity], stance: picked.picked } })
        }
        ui.box({ direction: 'row', gap: 2 }, () => {
          if (menuButton(ui, 'editor:ordering', t('editor.mouseOrder'), { active: ordering, height: 24, padding: [2, 6], tip: t('editor.mouseOrderTip') })) ordering = !ordering
          if (menuButton(ui, 'editor:clear', t('editor.clearTasks'), { height: 24, padding: [2, 6] })) edit({ op: 'clearTasks', entities })
        })
      }
      if (inventory && entities.length === 1) {
        ui.text(t('editor.stock', { capacity: inventory.capacity }), { color: COLORS.dim })
        for (const good of GOODS) {
          ui.box({ direction: 'row', align: 'center', justify: 'between', width: INNER }, () => {
            ui.text(t(`goods.${good}`))
            const amount = numberField(ui, `editor:stock:${good}`, inventory.items[good] ?? 0, { width: 90, min: 0, live: true })
            if (amount !== null) edit({ op: 'stock', entity: first, good, amount })
          })
        }
      }
    })
  }

  return {
    draw(ui, title, subtitle, top, footer) {
      const players = playersOf(sim)
      const selected = [...scene.selection].filter((entity) => sim.world.alive(entity) && entity !== ghost)
      const tile = hover ? describeTile(sim, hover.x, hover.y) : null
      const spots = picked()
      // Высота — от отступа сверху до низа экрана; длинное содержимое прокручивается.
      ui.box({ id: 'editor', anchor: { right: 8, top, bottom: 8 }, width: Math.min(EDITOR_WIDTH, ui.width - 32), skin: 'panel', padding: 12, direction: 'column', gap: 10, align: 'stretch' }, () => {
        ui.box({ direction: 'row', justify: 'between', gap: 8 }, () => {
          ui.text(title, { font: 'title', color: COLORS.bright })
          ui.box({ grow: 1, direction: 'row', justify: 'end' }, () => ui.text(subtitle, { color: COLORS.dim, ellipsis: true }))
        })
        ui.box({ id: 'editor:body', grow: 1, scroll: true, direction: 'column', gap: 10, align: 'stretch' }, () => {
          // Игрок: номер (и ник), новый игрок, его камера и кредиты.
          ui.box({ direction: 'column', gap: 6 }, () => {
            ui.text(t('editor.player'), { color: COLORS.dim })
            const options: [number, string][] = [[0, '0'], ...players.map((id): [number, string] => [id, label(id)]), [-1, '+']]
            const picked = chips(ui, 'editor:player', options, player, INNER, { small: true, tips: [t('editor.nobodyTip'), ...players.map(() => undefined), t('editor.newPlayer')] })
            if (picked?.picked === -1) {
              const next = Math.max(0, ...players) + 1
              if (edit({ op: 'addPlayer' })) player = next
              sync()
            } else if (picked) {
              player = picked.picked
              sync()
            }
            if (player > 0) {
              const camera = playerCamera(sim, player)
              ui.box({ direction: 'row', gap: 2 }, () => {
                if (menuButton(ui, 'editor:camera', camera ? t('editor.cameraHere') : t('editor.setCamera'), { height: 24, padding: [2, 6], tip: t('editor.cameraTip') })) {
                  edit({ op: 'camera', player, camera: { ...scene.camera.focus, zoom: scene.camera.zoom } })
                }
                if (camera) {
                  if (menuButton(ui, 'editor:toCamera', t('editor.toCamera'), { height: 24, padding: [2, 6] })) game.lookAt(camera.x, camera.y)
                  if (menuButton(ui, 'editor:dropCamera', t('editor.dropCamera'), { height: 24, padding: [2, 6] })) edit({ op: 'camera', player })
                }
              })
              ui.box({ direction: 'row', align: 'center', justify: 'between', width: INNER }, () => {
                ui.text(t('common.credits'))
                const credits = numberField(ui, 'editor:credits', creditsOf(sim, player), { width: 90, min: 0, live: true })
                if (credits !== null) edit({ op: 'credits', player, amount: credits })
              })
            }
          })

          // Инструменты и их настройки.
          ui.box({ direction: 'column', gap: 6 }, () => {
            const next = chips(ui, 'editor:tool', TOOLS.map((id) => [id, t(`editor.tools.${id}`)] as const), tool, INNER, { small: true })
            if (next) {
              tool = next.picked
              sync()
            }
            if (tool === 'paint') {
              const terrain = choice(ui, 'editor:terrain', t('editor.ground'), named(TERRAINS), brush.terrain)
              if (terrain) brush = { ...brush, terrain: terrain.picked }
              const biome = choice(ui, 'editor:biome', t('editor.biome'), named(BIOMES), brush.biome)
              if (biome) brush = { ...brush, biome: biome.picked }
              const tier = choice(ui, 'editor:tier', t('editor.tier'), TIERS.map((value): [number | undefined, string] => [value, value === undefined ? t('common.asIs') : String(value)]), brush.tier)
              if (tier) brush = { ...brush, tier: tier.picked }
              const cliff = choice(ui, 'editor:cliff', t('editor.edge'), named(CLIFFS), brush.cliff)
              if (cliff) brush = { ...brush, cliff: cliff.picked }
              const dunes = choice(ui, 'editor:dunes', t('editor.dunesOnSand'), named(DUNES), brush.dunes)
              if (dunes) brush = { ...brush, dunes: dunes.picked }
              const shape = choice(ui, 'editor:shape', t('editor.shape'), named(SHAPES), brush.shape ?? 'square')
              if (shape) brush = { ...brush, shape: shape.picked }
              ui.text(t('editor.brush', { size }), { color: COLORS.dim })
              size = slider(ui, 'editor:size', size, { min: 1, max: 15, step: 2, width: INNER })
              ui.text(`${t('editor.paintNote', { tiles: Math.round(MAX_PEAK_RADIUS * 2) })}${remote ? t('editor.paintNoteRemote') : t('editor.paintNoteLocal')}`, { color: COLORS.dim, wrap: INNER })
            }
            if (tool === 'building' || tool === 'unit') {
              // Список — в два столбца, прокручивается сам.
              const list: [string, string][] = tool === 'building' ? BUILDING_TYPES.map((type) => [type, t(`buildings.${type}`)]) : UNIT_TYPES.map((type) => [type, t(`units.${type}`)])
              const current = tool === 'building' ? building : unit
              const half = (INNER - 2) / 2
              ui.box({ id: 'editor:list', direction: 'column', gap: 2, maxHeight: Math.round(ui.height * 0.4), scroll: true }, () => {
                for (let i = 0; i < list.length; i += 2) {
                  ui.box({ direction: 'row', gap: 2 }, () => {
                    for (const [value, name] of list.slice(i, i + 2)) {
                      if (menuButton(ui, `editor:item:${value}`, name, { active: value === current, width: half, height: 26, padding: [2, 6] })) {
                        if (tool === 'building') building = value as BuildingType
                        else unit = value as UnitType
                        sync()
                      }
                    }
                  })
                }
              })
            }
            if (tool === 'deposit') {
              const picked = choice(ui, 'editor:kind', t('editor.newDeposit'), DEPOSIT_TYPES.map((type): [DepositKind, string] => [type, t(`goods.${type}`)]), kind)
              if (picked) {
                kind = picked.picked
                reserve = DEPOSIT_KINDS[kind].max
              }
              ui.box({ direction: 'row', align: 'center', justify: 'between', width: INNER }, () => {
                ui.text(t('editor.reserve'))
                const next = numberField(ui, 'editor:reserve', reserve, { width: 90, min: 0, live: true })
                if (next !== null) reserve = next
              })
              ui.text(t('editor.depositNote'), { color: COLORS.dim, wrap: INNER })
            }
            if (tool === 'select') ui.text(t('editor.selectNote'), { color: COLORS.dim, wrap: INNER })
          })

          if (tool === 'select' && spots.length) {
            ui.box({ direction: 'column', gap: 6 }, () => {
              ui.text(spots.length === 1 ? `${t(`goods.${spots[0].kind}`)} · ${spots[0].x}, ${spots[0].y}` : t('editor.deposits', { count: spots.length }), { color: COLORS.dim })
              const same = spots.every((spot) => spot.kind === spots[0].kind) ? spots[0].kind : undefined
              const picked = choice(ui, 'editor:spotKind', t('editor.kind'), DEPOSIT_TYPES.map((type): [DepositKind | undefined, string] => [type, t(`goods.${type}`)]), same)
              if (picked?.picked) edit({ op: 'setDeposit', spots: spotsOf(spots), kind: picked.picked })
              ui.box({ direction: 'row', align: 'center', justify: 'between', width: INNER }, () => {
                ui.text(spots.length > 1 ? t('editor.leftEach') : t('editor.left'))
                const left = numberField(ui, 'editor:left', reserveLeft(sim, spots[0].x, spots[0].y), { width: 90, min: 0, live: true })
                if (left !== null) edit({ op: 'setDeposit', spots: spotsOf(spots), left })
              })
            })
          }
          if (tool === 'select' && selected.length) inspector(ui, selected)
          if ((selected.length || spots.length) && tool === 'select' && menuButton(ui, 'editor:erase', t('editor.eraseSelected'), { danger: true })) eraseSelection()
        })

        // Подвал: что под указателем, ответ правки и кнопки.
        const where = tile && hover
          ? `${hover.x}, ${hover.y} · ${nameIn(TERRAINS, tile.terrain)} · ${nameIn(BIOMES, tile.biome)}${tile.tier === undefined ? '' : ` · ${t('editor.tierOf', { tier: tile.tier })}`}${tile.cliff ? ` · ${t('editor.cliff')}` : ''}${tile.foot ? ` · ${t('editor.foot')}` : ''}`
          : ''
        ui.box({ minHeight: 16 }, () => ui.text(`${where}${status ? `${where ? ' · ' : ''}${status}` : ''}`, { color: COLORS.dim, wrap: INNER }))
        ui.box({ direction: 'row', justify: 'end', gap: 4 }, footer)
      })
    },
    destroy() {
      window.removeEventListener('keydown', onKey)
      dropGhost()
      scene.edit = undefined
      scene.marks = []
      scene.depositGhost = null
      scene.placing = null
      scene.selectionBox = null
      scene.selection.clear()
    },
  }
}
