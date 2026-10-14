import type { Entity } from '../ecs'
import { setBlend } from '../gl'
import { ready, uploadAtlas, type PackedAtlas, type Sprite } from '../render/atlas'
import { Pixmap } from '../render/pixmap'
import type { Pass } from '../render/renderer'
import { createSpriteProgram, createSprites } from '../render/sprites'
import { BUILDINGS, Building, GOODS, Hauler, Owner, Position, Site, powerStates, shutDown, unlinked, unreachable, type Good, type PowerState } from '../sim'
import { GOOD_ICON, paintGood } from './goodIcons'
import { lacksOf, type Lack } from './lacks'
import { STATUS_BAR, STATUS_GAP, STATUS_STACK, statusBarsOf, statusLift } from './statusBars'
import type { Scene } from './scene'

/** Сторона картинки молнии в пикселях: молния растягивается на всё здание. */
const BOLT_SIZE = 32
/** Сколько разных молний нарисовано заранее. */
const BOLTS = 6
/** Сколько раз в секунду молния меняется и какую долю этих кадров её видно. */
const BOLT_RATE = 14
const BOLT_SHOWN = 0.6
const BOLT_GLOW = 0x4f9dff
const BOLT_CORE = 0xf2fbff
/** Насколько молния выше основания здания, в тайлах: разряды бьют над крышей. */
const BOLT_RISE = 0.5

/** Сторона значка нехватки энергии в пикселях спрайта и в тайлах на экране. */
const ICON_SIZE = 16
const ICON_TILES = 0.75
/** Значок мигает: столько секунд длится цикл и такую его долю значок виден. */
const ICON_PERIOD = 1
const ICON_SHOWN = 0.7


/** Простое воспроизводимое случайное число от 0 до 1 по двум целым. */
function noise(a: number, b: number) {
  const value = Math.sin(a * 127.1 + b * 311.7) * 43758.5453
  return value - Math.floor(value)
}

/** Рисует молнию: ломаная от края к краю картинки с короткой веткой, светлая сердцевина в синем ореоле. */
function drawBolt(index: number) {
  const image = new Pixmap(BOLT_SIZE, BOLT_SIZE)
  const points: number[] = []
  const JOINTS = 6
  // Молнии идут то сверху вниз, то наискось: начало и конец на разных краях.
  let x = 4 + noise(index, 1) * (BOLT_SIZE - 8)
  const endX = 4 + noise(index, 2) * (BOLT_SIZE - 8)
  for (let i = 0; i <= JOINTS; i++) {
    const along = i / JOINTS
    x += (endX - x) * along * 0.5 + (noise(index, 10 + i) - 0.5) * 12
    x = Math.max(2, Math.min(BOLT_SIZE - 3, x))
    points.push(Math.round(x), Math.round(2 + along * (BOLT_SIZE - 5)))
  }
  // Ветка от середины в сторону.
  const from = 2 * (1 + Math.floor(noise(index, 3) * (JOINTS - 2)))
  const side = noise(index, 4) < 0.5 ? -1 : 1
  const branch = [
    points[from], points[from + 1],
    points[from] + side * 5, points[from + 1] + 3,
    points[from] + side * 7, points[from + 1] + 8,
  ]
  for (const [line, width, color] of [[points, 3, BOLT_GLOW], [branch, 2, BOLT_GLOW], [points, 1, BOLT_CORE], [branch, 1, BOLT_CORE]] as const) {
    for (let i = 0; i + 3 < line.length; i += 2) image.line(line[i], line[i + 1], line[i + 2], line[i + 3], width, color)
  }
  return image
}

/** Рисует значок нехватки энергии: жёлтая молния на красном круге. */
function drawIcon() {
  const image = new Pixmap(ICON_SIZE, ICON_SIZE)
  image.circle(8, 8, 8, 0x0b111b)
  image.circle(8, 8, 7, 0xc8321f)
  image.circle(7.5, 7.5, 5.5, 0xe5492f)
  for (const [width, color] of [[3, 0x0b111b], [1, 0xffe066]] as const) {
    image.line(10, 3, 6, 8, width, color)
    image.line(6, 8, 10, 8, width, color)
    image.line(10, 8, 6, 13, width, color)
  }
  return image
}

/** Значок «Не подключено к сети»: разорванная труба на оранжевом круге. */
function drawLoose() {
  const image = new Pixmap(ICON_SIZE, ICON_SIZE)
  image.circle(8, 8, 8, 0x0b111b)
  image.circle(8, 8, 7, 0xd9822b)
  image.circle(7.5, 7.5, 5.5, 0xf09a3e)
  // Два обрубка трубы с зазором посередине.
  for (const [x, width] of [[2, 4], [10, 4]] as const) {
    image.rect(x, 6, width, 5, 0x0b111b)
    image.rect(x, 7, width, 3, 0xdfe6ee)
  }
  return image
}

/** Значок «Выключено»: знак питания на сером круге. Не тревога — не мигает. */
function drawOff() {
  const image = new Pixmap(ICON_SIZE, ICON_SIZE)
  image.circle(8, 8, 8, 0x0b111b)
  image.circle(8, 8, 7, 0x46505f)
  image.circle(7.5, 7.5, 5.5, 0x5d6878)
  // Разомкнутое сверху кольцо и черта в разрыве.
  image.ring(8, 8.5, 3.5, 1.4, 0xdfe6ee)
  image.rect(6, 3, 4, 4, 0x5d6878)
  image.rect(7, 3, 2, 6, 0xdfe6ee)
  return image
}

/** Значок «Не хватает груза»: мини-иконка этого груза на тёмном круге в красном кольце. */
function drawLack(good: Good) {
  const image = new Pixmap(ICON_SIZE, ICON_SIZE)
  image.circle(8, 8, 8, 0x0b111b)
  image.circle(8, 8, 7, 0xe5492f)
  image.circle(8, 8, 6, 0x121c2a)
  const shift = (ICON_SIZE - GOOD_ICON) / 2
  paintGood(good, (x, y, [red, green, blue]) => image.rect(shift + x, shift + y, 1, 1, (red << 16) | (green << 8) | blue))
  return image
}

/** Значок «Не хватает кредитов»: золотая монета на тёмном круге в красном кольце, как у груза. */
function drawBroke() {
  const image = new Pixmap(ICON_SIZE, ICON_SIZE)
  image.circle(8, 8, 8, 0x0b111b)
  image.circle(8, 8, 7, 0xe5492f)
  image.circle(8, 8, 6, 0x121c2a)
  image.circle(8, 8, 4, 0x8a5a14)
  image.circle(8, 8, 3.2, 0xf0c95a)
  image.rect(7, 6, 2, 4, 0x8a5a14)
  return image
}

/** Значок «Не подъехать»: белая черта на красном круге, как знак «въезд запрещён». Над зданием и запертым грузовиком. */
function drawBlocked() {
  const image = new Pixmap(ICON_SIZE, ICON_SIZE)
  image.circle(8, 8, 8, 0x0b111b)
  image.circle(8, 8, 7, 0xc8321f)
  image.circle(7.5, 7.5, 5.5, 0xe5492f)
  image.rect(3, 6, 10, 4, 0x0b111b)
  image.rect(4, 7, 8, 2, 0xf2f5f8)
  return image
}

/** Насколько значок запертого грузовика выше его середины, в тайлах. */
const TRUCK_ICON_RISE = 0.9

type Color = readonly [number, number, number]
const BAR_BACK: Color = [0.03, 0.05, 0.08]
const BAR_GOOD: Color = [0.45, 0.9, 0.55]
const BAR_BAD: Color = [1, 0.35, 0.25]
/** Запас щита генератора. */
const BAR_SHIELD: Color = [0.3, 0.55, 1]

/** Молнии, значки и белый квадрат для полосок. */
export function powerSprites(): Sprite[] {
  const bolts = Array.from({ length: BOLTS }, (_, i) => drawBolt(i))
  const white = new Pixmap(4, 4).rect(0, 0, 4, 4, 0xffffff)
  return [...bolts, drawIcon(), white, drawLoose(), drawOff(), ...GOODS.map(drawLack), drawBroke(), drawBlocked()].map(ready)
}

/** Раз во сколько тиков симуляции пересчитывается, каким своим зданиям чего не хватает: это обход складов и сетей. */
const LACK_TICKS = 10

/**
 * Проход тревог над зданиями: молнии перегруза на электростанциях, значок над зданиями, которые из-за нехватки
 * энергии работают медленнее, значок «Не подключено к сети» над своими зданиями со складом без труб, значок груза
 * над своими зданиями и стройками, которые стоят без него (см. lacks.ts), значок «Не подъехать» над своими зданиями,
 * к которым не подъехать грузовикам, и над запертыми грузовиками, значок «Выключено» над своими
 * выключенными, над зданиями — полоски прочности повреждённых и синие полоски запаса генераторов щита,
 * см. statusBars.ts. Ставить выше освещения, чтобы ночью не темнело.
 */
export function createPowerPass(gl: WebGL2RenderingContext, scene: Scene, art: PackedAtlas): Pass {
  const atlas = uploadAtlas(gl, art)
  const boltFrames = atlas.frames.slice(0, BOLTS)
  const iconFrame = atlas.frames[BOLTS]
  const whiteFrame = atlas.frames[BOLTS + 1]
  const looseFrame = atlas.frames[BOLTS + 2]
  const offFrame = atlas.frames[BOLTS + 3]
  const lackFrames = new Map<Lack, (typeof atlas.frames)[number]>(GOODS.map((good, i) => [good, atlas.frames[BOLTS + 4 + i]]))
  lackFrames.set('credits', atlas.frames[BOLTS + 4 + GOODS.length])
  const blockedFrame = atlas.frames[BOLTS + 5 + GOODS.length]
  const whiteU = whiteFrame.u + whiteFrame.width / 2
  const whiteV = whiteFrame.v + whiteFrame.height / 2

  const program = createSpriteProgram(gl)
  const sprites = createSprites(gl, program)

  // Кому не хватает энергии, пересчитывается раз в тик симуляции, а не каждый кадр.
  let states = new Map<Entity, PowerState>()
  let tick = -1
  let lacks = new Map<Entity, Lack>()
  let lacksAt = -Infinity
  let lacksSim: unknown

  return {
    draw({ camera, width, height, time, view }) {
      const { sim } = scene
      const { world } = sim
      if (sim.time.tick !== tick) {
        tick = sim.time.tick
        states = powerStates(sim)
      }
      if (lacksSim !== sim || Math.abs(sim.time.tick - lacksAt) >= LACK_TICKS) {
        lacks = lacksOf(sim, scene.player)
        lacksAt = sim.time.tick
        lacksSim = sim
      }
      const halfWidth = width / 2 / camera.zoom + 2
      const halfHeight = height / 2 / camera.zoom + 2
      const step = Math.floor(time * BOLT_RATE)
      const iconOn = (time % ICON_PERIOD) / ICON_PERIOD < ICON_SHOWN
      const barHeight = STATUS_BAR / camera.zoom
      const stack = STATUS_STACK / camera.zoom

      sprites.clear()
      for (const [entity, position, building] of world.query(Position, Building)) {
        const spec = BUILDINGS[building.type]
        const x = position.x - camera.x
        const y = position.y - camera.y
        if (x + spec.width < -halfWidth || x > halfWidth || y + spec.height < -halfHeight || y > halfHeight) continue

        const state = states.get(entity)
        if (state === 'overload' && noise(step, entity) < BOLT_SHOWN) {
          const frame = boltFrames[Math.floor(noise(entity, step) * BOLTS)]
          // Каждая вторая молния отражена, чтобы шести картинок хватало надолго.
          const flip = noise(step + 7, entity) < 0.5
          sprites.push(
            x, y - BOLT_RISE, spec.width, spec.height + BOLT_RISE,
            flip ? frame.u + frame.width : frame.u, frame.v, flip ? -frame.width : frame.width, frame.height,
            1, 1, 1, 1,
          )
        }
        // Значки — над полосами состояния, несколько сразу — рядом, а не друг на друге. Тревожные мигают, а место
        // держат и пока погашены; «Выключено» горит ровно.
        const lifted = y - statusLift(world, entity, camera.zoom)
        const icons: [frame: typeof iconFrame, shown: boolean][] = []
        if (unlinked(sim, scene.player, entity)) icons.push([looseFrame, iconOn])
        if (unreachable(sim, entity) && world.get(entity, Owner)?.player === scene.player) icons.push([blockedFrame, iconOn])
        if (state === 'starved') icons.push([iconFrame, iconOn])
        const lack = lacks.get(entity)
        if (lack) icons.push([lackFrames.get(lack)!, iconOn])
        if (shutDown(sim, scene.player, entity)) icons.push([offFrame, true])
        icons.forEach(([frame, shown], i) => {
          if (!shown) return
          sprites.push(
            x + spec.width / 2 + (i - icons.length / 2) * ICON_TILES, lifted - ICON_TILES / 2, ICON_TILES, ICON_TILES,
            frame.u, frame.v, frame.width, frame.height,
            1, 1, 1, 1,
          )
        })
        // Полосы над крышей, сверху вниз: прочность, под ней запас щита. Нижняя — вплотную к крыше.
        const { health, shield } = statusBarsOf(world, entity)
        const rows: [number, Color][] = []
        if (health !== undefined) rows.push([health, health > 0.5 ? BAR_GOOD : BAR_BAD])
        if (shield !== undefined) rows.push([shield, BAR_SHIELD])
        rows.forEach(([share, color], i) => {
          const top = y - STATUS_GAP - (rows.length - i) * barHeight - (rows.length - 1 - i) * stack
          sprites.push(x, top, spec.width, barHeight, whiteU, whiteV, 0, 0, ...BAR_BACK, 1)
          sprites.push(x, top, spec.width * share, barHeight, whiteU, whiteV, 0, 0, ...color, 1)
        })
      }

      // Запертые свои грузовики: им не выехать, пока не разберут то, чем их обстроили.
      if (iconOn) {
        for (const [, hauler, position, owner] of world.query(Hauler, Position, Owner)) {
          if (!hauler.trapped || owner.player !== scene.player) continue
          const x = position.x - camera.x
          const y = position.y - camera.y
          if (x < -halfWidth || x > halfWidth || y < -halfHeight || y > halfHeight) continue
          const frame = blockedFrame
          sprites.push(x - ICON_TILES / 2, y - TRUCK_ICON_RISE - ICON_TILES / 2, ICON_TILES, ICON_TILES, frame.u, frame.v, frame.width, frame.height, 1, 1, 1, 1)
        }
      }

      // Площадки, к которым строитель ещё не приступил: здания на них нет, а материалов они уже ждут.
      for (const [entity, lack] of lacks) {
        if (world.has(entity, Building)) continue
        const site = world.get(entity, Site)
        const position = world.get(entity, Position)
        if (!site || !position || !iconOn) continue
        const spec = BUILDINGS[site.type]
        const x = position.x - camera.x
        const y = position.y - camera.y
        if (x + spec.width < -halfWidth || x > halfWidth || y + spec.height < -halfHeight || y > halfHeight) continue
        const frame = lackFrames.get(lack)!
        sprites.push(x + spec.width / 2 - ICON_TILES / 2, y - ICON_TILES / 2, ICON_TILES, ICON_TILES, frame.u, frame.v, frame.width, frame.height, 1, 1, 1, 1)
      }

      setBlend(gl, 'alpha')
      program.use(view, { uTexture: atlas.texture })
      sprites.draw()
    },
    destroy() {
      sprites.destroy()
      program.destroy()
      atlas.texture.destroy()
    },
  }
}
