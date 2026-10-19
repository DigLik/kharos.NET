/**
 * Описания сцен сверки кадров (Н6.4, уровень 2). Один и тот же JSON читают TS (page.ts рисует его настоящим клиентом) и C# (Viewer
 * собирает ту же сцену на реплике хоста). Чистые данные и поиск мест: ни DOM, ни клиента здесь нет — файл грузится и в bun, и в
 * страницу.
 *
 * Мир: createSim({ generator: DEFAULT_CONFIG, size, fog, правила по умолчанию }). Порядок сборки:
 *   1. credits — addCredits(player, amount): так у игрока появляется запись Player; research — grantResearch(player).
 *   2. entities по порядку: building — placeBuilding(type, x, y, owner) (x, y — левый верхний тайл основания), unit —
 *      spawnUnit(type, owner, x, y) (тайл; юнит встаёт в его середину). Номер в списке — «индекс сущности» для commands и selected.
 *   3. allies — offerAlliance в обе стороны (союз заключён).
 *   4. commands по порядку: sim.send(player, command); поля units и builders — индексы сущностей, building и site, если это число, — тоже (у build поле building — строка типа); в команде
 *      сущность подставляется настоящим номером.
 *   5. advanceTicks тиков симуляции подряд (целое число тиков, без дроби), затем кадр.
 * Кадр: камера (x, y) — центр экрана в тайлах, zoom пикселей на тайл, размер width × height, weather (hour, light, wind, осадки —
 * ровно эти числа, не weatherAt), time — замороженные экранные часы в секундах (аргумент time проходов), delta = 0,
 * selected — выделенные юниты своего игрока. terrainRegions — прямоугольники кадра (x, y, w, h в пикселях), где только земля:
 * их сверка идёт по долям палитры, остальное — по пикселям. hud: true — поверх кадра рисуется интерфейс игры (панели HUD).
 */
import { Biome, DEFAULT_CONFIG, Terrain, sampleTerrain } from '../../../../packages/shared/src/terrain'

export interface EntitySpec {
  kind: 'building' | 'unit'
  type: string
  x: number
  y: number
  owner: number
}

export interface CommandSpec {
  player: number
  command: Record<string, unknown>
}

export interface WeatherSpec {
  hour: number
  light: number
  windX: number
  windY: number
  precipitation: number
}

export interface SceneSpec {
  name: string
  description: string
  seed: number
  size: number
  fog: boolean
  player: number
  width: number
  height: number
  camera: { x: number; y: number; zoom: number }
  weather: WeatherSpec
  time: number
  credits: { player: number; amount: number }[]
  research: number[]
  entities: EntitySpec[]
  allies: [number, number][]
  commands: CommandSpec[]
  advanceTicks: number
  selected: number[]
  terrainRegions: [number, number, number, number][]
  hud: boolean
  /** Для справки: биом места. */
  biome: number
}

export const WIDTH = 640
export const HEIGHT = 400
export const SIZE = 1024
export const SEED = DEFAULT_CONFIG.seed
const OWN = 1
const FOE = 2
const ALLY = 3
const NEUTRAL = 0

/** Освещение по часу, как weatherAt без смены погоды (dayLength 0): та же формула, что в packages/sim-ts/src/weather.ts. */
function lightAt(hour: number) {
  const smooth = (t: number) => t * t * (3 - 2 * t)
  const sun = Math.sin(((hour - 6) / 12) * Math.PI)
  const day = smooth(Math.max(0, Math.min(1, (sun + 0.2) / 0.6)))
  return 0.1 + 0.9 * day
}

const weather = (hour: number, precipitation = 0, windX = 1.5, windY = 0.4): WeatherSpec => {
  const rain = precipitation > 0 ? 1 - 0.35 * precipitation : 1
  return { hour, light: Math.fround(lightAt(hour) * rain), windX, windY, precipitation }
}

/** Участок 40 × 26 тайлов вокруг (x, y), где всё — биом biome, а в середине 24 × 14 нет ни гор, ни болот. */
function suitable(biome: number, x: number, y: number) {
  for (let dy = -13; dy <= 13; dy += 3) {
    for (let dx = -20; dx <= 20; dx += 4) {
      const at = sampleTerrain(DEFAULT_CONFIG, x + dx, y + dy)
      if (at.biome !== biome) return false
      const core = Math.abs(dx) <= 12 && Math.abs(dy) <= 7
      if (core && at.terrain !== Terrain.Sand && at.terrain !== Terrain.Rock) return false
      if (!core && at.terrain === Terrain.Mountain) return false
    }
  }
  return true
}

/** Топи сплошь в воде: годится любой участок биома без гор. */
function marshy(x: number, y: number) {
  for (let dy = -13; dy <= 13; dy += 3) {
    for (let dx = -20; dx <= 20; dx += 4) {
      const at = sampleTerrain(DEFAULT_CONFIG, x + dx, y + dy)
      if (at.biome !== Biome.Marsh || at.terrain === Terrain.Mountain) return false
    }
  }
  return true
}

/** Ближайшее к началу мира место биома biome, где можно ставить сцену. */
export function spotOf(biome: number): { x: number; y: number } {
  for (let ring = 0; ring < 120; ring++) {
    for (let gy = -ring; gy <= ring; gy++) {
      for (let gx = -ring; gx <= ring; gx++) {
        if (Math.max(Math.abs(gx), Math.abs(gy)) !== ring) continue
        const x = gx * 12
        const y = gy * 12
        if (biome === Biome.Marsh ? marshy(x, y) : suitable(biome, x, y)) return { x, y }
      }
    }
  }
  throw new Error(`нет места для биома ${biome}`)
}

const building = (type: string, x: number, y: number, owner = OWN): EntitySpec => ({ kind: 'building', type, x, y, owner })
const unit = (type: string, x: number, y: number, owner = OWN): EntitySpec => ({ kind: 'unit', type, x, y, owner })

/** Общая застройка для сцен-образцов: база в середине кадра и несколько юнитов (индексы 8–11 — юниты). */
function base(cx: number, cy: number): EntitySpec[] {
  return [
    building('command', cx - 7, cy - 4),
    building('generator', cx - 3, cy - 4),
    building('smelter', cx - 7, cy),
    building('factory', cx + 1, cy - 4),
    building('turret', cx + 5, cy - 2),
    building('wall', cx + 4, cy + 1),
    building('wall', cx + 5, cy + 1),
    building('wall', cx + 6, cy + 1),
    unit('infantry', cx - 3, cy + 3),
    unit('buggy', cx - 1, cy + 4),
    unit('tank', cx + 2, cy + 4),
    unit('truck', cx - 5, cy + 4),
  ]
}

/** Углы кадра 640 × 400 без застройки: 128 × 80 в каждом. */
const CORNERS: [number, number, number, number][] = [
  [0, 0, 128, 80],
  [WIDTH - 128, 0, 128, 80],
  [0, HEIGHT - 80, 128, 80],
  [WIDTH - 128, HEIGHT - 80, 128, 80],
]

function make(partial: Partial<SceneSpec> & Pick<SceneSpec, 'name' | 'description' | 'camera' | 'weather' | 'entities'>): SceneSpec {
  return {
    seed: SEED,
    size: SIZE,
    fog: false,
    player: OWN,
    width: WIDTH,
    height: HEIGHT,
    time: 10,
    credits: [OWN, FOE, ALLY].map((player) => ({ player, amount: 10000 })),
    research: [OWN, FOE, ALLY],
    allies: [],
    commands: [],
    advanceTicks: 1,
    selected: [],
    terrainRegions: CORNERS,
    hud: false,
    biome: Biome.Erg,
    ...partial,
  }
}

const NOON = 12
const NIGHT = 23

export function scenes(): SceneSpec[] {
  const out: SceneSpec[] = []
  const biomes: [string, number][] = [['erg', Biome.Erg], ['salt', Biome.SaltFlats], ['red', Biome.RedWastes], ['marsh', Biome.Marsh]]
  const spots = Object.fromEntries(biomes.map(([name, id]) => [name, spotOf(id)]))

  // 1–4. Четыре биома в полдень.
  for (const [name, id] of biomes) {
    const { x, y } = spots[name]
    out.push(make({
      name: `biome-${name}-noon`,
      description: `Биом ${name}, полдень, застройка и четыре юнита`,
      biome: id,
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NOON),
      entities: base(x, y),
    }))
  }

  // 5. Ночь: огни зданий и фары. Юниты едут, чтобы лучи легли вдоль курса.
  {
    const { x, y } = spots.erg
    const entities = [...base(x, y), unit('lancer', x + 4, y + 4), unit('gunship', x - 2, y - 6)]
    out.push(make({
      name: 'night-lights',
      description: 'Ночь (23 ч): огни зданий, фары и пятна света, юниты в движении',
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NIGHT),
      entities,
      commands: [{ player: OWN, command: { type: 'move', units: [8, 9, 10, 11, 12], x: x + 8, y: y + 8 } }],
      advanceTicks: 10,
    }))
  }

  // 6. Туман войны: открыто только вокруг своих; чужие и ничьи спрятаны.
  {
    const { x, y } = spots.red
    const entities = [...base(x, y), unit('tank', x + 12, y + 2, FOE), building('turret', x + 14, y - 2, FOE)]
    out.push(make({
      name: 'fog',
      description: 'Туман войны: видимая область вокруг своей базы, дальше тёмная; чужие на границе',
      biome: Biome.RedWastes,
      fog: true,
      camera: { x: x + 4.5, y: y + 0.5, zoom: 32 },
      weather: weather(NOON),
      entities,
      advanceTicks: 20,
      terrainRegions: [[0, 0, 100, 60]],
    }))
  }

  // 7–9. Осадки по биомам: пыль в эрге, снег на солончаках, дождь в топях.
  for (const [name, kind] of [['erg', 'dust'], ['salt', 'snow'], ['marsh', 'rain']] as const) {
    const { x, y } = spots[name]
    out.push(make({
      name: `weather-${kind}`,
      description: `Осадки (${kind}), сила 0.8, сильный ветер`,
      biome: biomes.find(([n]) => n === name)![1],
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(15, 0.8, 2.4, 0.9),
      entities: base(x, y).slice(0, 8),
      time: 25,
    }))
  }

  // 10. Колея и остовы: техника проехала путь и повоевала, на земле гарь и обломки.
  {
    const { x, y } = spots.erg
    const entities = [
      unit('buggy', x - 8, y - 3), unit('buggy', x - 8, y), unit('tank', x - 8, y + 3), unit('infantry', x - 8, y + 5), unit('lancer', x - 8, y + 6),
      unit('tank', x + 9, y - 2, FOE), unit('tank', x + 9, y + 2, FOE),
    ]
    out.push(make({
      name: 'tracks-wrecks',
      description: 'Колея и остовы: техника едет через кадр и воюет, на земле гарь и обломки',
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NOON),
      entities,
      commands: [
        { player: OWN, command: { type: 'move', units: [0, 1, 2, 3, 4], x: x + 2, y: y + 1 } },
        { player: OWN, command: { type: 'stance', units: [0, 1, 2, 3, 4], stance: 'aggressive' } },
        { player: FOE, command: { type: 'stance', units: [5, 6], stance: 'aggressive' } },
      ],
      advanceTicks: 300,
    }))
  }

  // 11. Стороны и серые ничьи: свои, чужие, союзные и ничьи здания и юниты, выделение.
  {
    const { x, y } = spots.salt
    const entities = [
      building('generator', x - 8, y - 4, OWN), building('generator', x - 4, y - 4, FOE), building('generator', x, y - 4, ALLY), building('generator', x + 4, y - 4, NEUTRAL),
      unit('tank', x - 8, y + 2, OWN), unit('tank', x - 4, y + 2, FOE), unit('tank', x, y + 2, ALLY), unit('tank', x + 4, y + 2, NEUTRAL),
      unit('infantry', x - 7, y + 5, OWN), unit('infantry', x - 3, y + 5, FOE), unit('infantry', x + 1, y + 5, ALLY), unit('infantry', x + 5, y + 5, NEUTRAL),
      building('turret', x + 8, y - 4, OWN), building('turret', x + 8, y - 1, FOE), building('turret', x + 8, y + 2, NEUTRAL),
    ]
    out.push(make({
      name: 'sides-neutrals',
      description: 'Стороны: свои синие, чужие красные, союзные зелёные, ничьи серые; свой танк и пехота выделены',
      biome: Biome.SaltFlats,
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NOON),
      entities,
      allies: [[OWN, ALLY]],
      selected: [4, 8],
    }))
  }

  // 12. Стройка и выключенное: площадки разной готовности и выключенные здания.
  {
    const { x, y } = spots.red
    const entities = [
      building('command', x - 9, y - 4),
      building('generator', x - 4, y - 4),
      building('smelter', x - 4, y),
      building('siliconWorks', x + 1, y - 4),
      unit('builder', x - 6, y + 4), unit('builder', x - 1, y + 4), unit('builder', x + 3, y + 4),
    ]
    out.push(make({
      name: 'construction-off',
      description: 'Стройка (три площадки, строители) и выключенные здания (темнее, без огней), вечер',
      biome: Biome.RedWastes,
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NIGHT),
      entities,
      commands: [
        { player: OWN, command: { type: 'work', building: 2, on: false } },
        { player: OWN, command: { type: 'work', building: 3, on: false } },
        { player: OWN, command: { type: 'build', building: 'radar', x: x + 4, y, builders: [4] } },
        { player: OWN, command: { type: 'build', building: 'generator', x: x + 6, y: y - 2, builders: [5] } },
        { player: OWN, command: { type: 'build', building: 'matter', x: x - 1, y: y + 1, builders: [6] } },
      ],
      advanceTicks: 90,
    }))
  }

  // 13. Бой: две стороны сходятся, на кадре вспышки, снаряды и взрывы.
  {
    const { x, y } = spots.red
    const types = ['tank', 'rocketeer', 'infantry', 'lancer', 'tesla']
    const entities: EntitySpec[] = []
    for (let i = 0; i < 5; i++) entities.push(unit(types[i], x - 5, y - 4 + i * 2, OWN))
    for (let i = 0; i < 5; i++) entities.push(unit(types[i], x + 5, y - 4 + i * 2, FOE))
    out.push(make({
      name: 'battle-blast',
      description: 'Бой стенка на стенку, кадр со вспышками, снарядами и взрывами',
      biome: Biome.RedWastes,
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NOON),
      entities,
      commands: [
        { player: OWN, command: { type: 'stance', units: [0, 1, 2, 3, 4], stance: 'aggressive' } },
        { player: FOE, command: { type: 'stance', units: [5, 6, 7, 8, 9], stance: 'aggressive' } },
        { player: OWN, command: { type: 'move', units: [0, 1, 2, 3, 4], x: x - 1, y } },
        { player: FOE, command: { type: 'move', units: [5, 6, 7, 8, 9], x: x + 1, y } },
      ],
      advanceTicks: 100,
      terrainRegions: [[0, 0, 100, 60]],
    }))
  }

  // 14. Панели HUD: игра с базой и интерфейсом поверх.
  {
    const { x, y } = spots.erg
    out.push(make({
      name: 'hud-panels',
      description: 'Интерфейс игры: верхняя полоса, нижняя панель с выделенным юнитом и сеткой команд',
      camera: { x: x + 0.5, y: y + 0.5, zoom: 32 },
      weather: weather(NOON),
      entities: base(x, y),
      selected: [9],
      hud: true,
      terrainRegions: [],
    }))
  }
  return out
}
