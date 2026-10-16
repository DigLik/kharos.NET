// Главное меню на холсте поверх слайдов симуляции (showcase.ts): продолжить последнюю игру, начать новую, сохранения,
// сеть, настройки, об игре. Перенесено со страницы (src/ui/Menu.tsx) как было: те же окна, поля и кнопки.

import { createMenuMusic, type MenuMusic } from '../../audio/music'
import { createSlot, deleteSave, exportSave, importSave, listSaves, renameSave, storeSave, type SaveSlot } from '../../game/storage'
import { t, currentLang, type Key } from '@kharos/i18n'
import { DEFAULT_SETTINGS } from '../../map/settings'
import { DEFAULT_CONFIG, PEAK_RADIUS_LIMIT, type GeneratorConfig } from '../../map/terrain'
import { isInsecure } from '../../net/connect'
import { NAME_LENGTH, PROTOCOL_VERSION, cleanName } from '../../net/protocol'
import { DEFAULT_WEATHER, createSim } from '../../sim'
import { forgetServer, lastLaunch, localServerUrl, recentServers, type Launch } from '../../ui/launch'
import { checkbox, chips, details, link, menuButton, menuWindow, numberField, row, slider, textField } from '../form'
import { createPreview, type Preview } from '../preview'
import type { Field as InputField, Ui } from '../ui'
import { COLORS } from '../widgets'
import { download, pickFile } from '../files'
import { settingsWindow } from './settings'

/** Симуляция идёт 20 тиков в секунду. */
const TICKS_PER_SECOND = 20
/** Какой стороны бывает карта новой игры, в тайлах. */
const MAP_SIZES = [512, 1024, 2048]
/** Сколько длятся сутки новой игры, в секундах; 0 — время стоит. */
const DAY_LENGTHS = [600, 1200, 2400, 0]
const clock = (hour: number) => `${String(Math.floor(hour)).padStart(2, '0')}:${String(Math.floor((hour % 1) * 60)).padStart(2, '0')}`
const SERVER_KEY = 'kharos.server'
/** Страница проекта и кто делает игру. */
const REPOSITORY = 'https://github.com/vicimpa/kharos'
const AUTHORS = [{ name: 'vicimpa', url: 'https://github.com/vicimpa' }]
/** Пределы стороны новой карты в тайлах. */
const WORLD_MIN = 64
const WORLD_MAX = 2048
/** Уже скольких пикселей экран узкий: окна по центру, превью над полями. */
const NARROW = 760

type Screen = 'main' | 'new' | 'saves' | 'network' | 'settings' | 'about' | 'world'

const randomSeed = () => Math.floor(Math.random() * 2 ** 31)

const playtime = (tick: number) => {
  const minutes = Math.floor(tick / TICKS_PER_SECOND / 60)
  return minutes < 60 ? t('menu.minutes', { minutes }) : t('menu.hours', { hours: Math.floor(minutes / 60), minutes: minutes % 60 })
}

const date = (time: number) => new Date(time).toLocaleString(currentLang(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

function loadServer() {
  try {
    const saved = JSON.parse(localStorage.getItem(SERVER_KEY) ?? 'null') as { url?: unknown; lag?: unknown; name?: unknown } | null
    return {
      url: typeof saved?.url === 'string' ? saved.url : localServerUrl(),
      lag: typeof saved?.lag === 'number' ? saved.lag : 0,
      name: typeof saved?.name === 'string' ? saved.name : '',
    }
  } catch {
    return { url: localServerUrl(), lag: 0, name: '' }
  }
}

/** Ползунок местности для человека: доля от 0 до 1 переводится в параметр генератора и обратно. */
interface Knob {
  key: 'rockLevel' | 'swampLevel' | 'zoneScale' | 'zoneWarp' | 'biomeScale' | 'peakChance'
  from: number
  to: number
}

const KNOBS: Knob[] = [
  // Чем ниже уровень скал, тем их больше: ползунок «больше» вправо.
  { key: 'rockLevel', from: 0.75, to: 0.45 },
  { key: 'swampLevel', from: 0.2, to: 0.5 },
  { key: 'zoneScale', from: 24, to: 120 },
  { key: 'zoneWarp', from: 0, to: 80 },
  { key: 'biomeScale', from: 80, to: 600 },
  { key: 'peakChance', from: 0, to: 1 },
]

/** Готовые местности: отличия от генератора по умолчанию. */
const PRESETS: { label: Key; generator: Partial<GeneratorConfig> }[] = [
  { label: 'menu.presets.normal', generator: {} },
  { label: 'menu.presets.rocky', generator: { rockLevel: 0.5, swampLevel: 0.3, peakChance: 0.5 } },
  { label: 'menu.presets.desert', generator: { rockLevel: 0.68, swampLevel: 0.25, zoneScale: 80 } },
  { label: 'menu.presets.swamps', generator: { rockLevel: 0.62, swampLevel: 0.46, zoneWarp: 50 } },
  { label: 'menu.presets.islands', generator: { rockLevel: 0.6, swampLevel: 0.5, zoneScale: 40, zoneWarp: 10 } },
]

const knobValue = (knob: Knob, config: GeneratorConfig) => Math.max(0, Math.min(1, ((config[knob.key] as number) - knob.from) / (knob.to - knob.from)))

/** Отличия местности от генератора по умолчанию, без зерна: их и хранит слот. */
function generatorChanges(config: GeneratorConfig) {
  const changes: Partial<GeneratorConfig> = {}
  for (const key of Object.keys(DEFAULT_CONFIG) as (keyof GeneratorConfig)[]) if (key !== 'seed' && config[key] !== DEFAULT_CONFIG[key]) Object.assign(changes, { [key]: config[key] })
  return changes
}

/** Поле тонкой настройки генератора: ползунок одного числового параметра. */
interface Field {
  key: keyof GeneratorConfig
  label: Key
  min: number
  max: number
  step: number
}

const biomeFields = (prefix: 'salt' | 'red' | 'marsh'): Field[] => [
  { key: `${prefix}SwampShift`, label: 'generator.fields.swampShift', min: -0.2, max: 0.2, step: 0.005 },
  { key: `${prefix}RockShift`, label: 'generator.fields.rockShift', min: -0.2, max: 0.2, step: 0.005 },
  { key: `${prefix}PeakFactor`, label: 'generator.fields.peakFactor', min: 0, max: 4, step: 0.1 },
]

/** Группы тонкой настройки генератора. */
const GENERATOR_GROUPS: { title: Key; fields: Field[] }[] = [
  {
    title: 'generator.groups.zones',
    fields: [
      { key: 'biomeScale', label: 'generator.fields.biomeScale', min: 40, max: 600, step: 10 },
      { key: 'zoneScale', label: 'generator.fields.zoneScale', min: 16, max: 160, step: 1 },
      { key: 'zoneWarp', label: 'generator.fields.zoneWarp', min: 0, max: 80, step: 1 },
      { key: 'swampLevel', label: 'generator.fields.swampLevel', min: 0, max: 1, step: 0.005 },
      { key: 'rockLevel', label: 'generator.fields.rockLevel', min: 0, max: 1, step: 0.005 },
    ],
  },
  {
    title: 'generator.groups.peaks',
    fields: [
      { key: 'peakChance', label: 'generator.fields.chance', min: 0, max: 1, step: 0.01 },
      { key: 'peakMinRadius', label: 'generator.fields.peakMinRadius', min: 0.5, max: PEAK_RADIUS_LIMIT, step: 0.05 },
      { key: 'peakMaxRadius', label: 'generator.fields.peakMaxRadius', min: 0.5, max: PEAK_RADIUS_LIMIT, step: 0.05 },
      { key: 'doubleChance', label: 'generator.fields.doubleChance', min: 0, max: 1, step: 0.01 },
      { key: 'tripleChance', label: 'generator.fields.tripleChance', min: 0, max: 1, step: 0.01 },
    ],
  },
  {
    title: 'generator.groups.deposits',
    fields: [
      { key: 'depositChance', label: 'generator.fields.chance', min: 0, max: 1, step: 0.01 },
      { key: 'depositRichness', label: 'generator.fields.depositRichness', min: 0.1, max: 5, step: 0.1 },
    ],
  },
  { title: 'generator.groups.salt', fields: biomeFields('salt') },
  { title: 'generator.groups.red', fields: biomeFields('red') },
  { title: 'generator.groups.marsh', fields: biomeFields('marsh') },
]

/** Ползунки тонкой настройки по группам: каждая — в утопленной рамке. Возвращает новые значения или null. */
function generatorGroups(ui: Ui, values: GeneratorConfig, width: number): GeneratorConfig | null {
  let next: GeneratorConfig | null = null
  for (const group of GENERATOR_GROUPS) {
    ui.box({ skin: 'well', padding: [8, 10], direction: 'column', gap: 6, width }, () => {
      ui.text(t(group.title), { color: COLORS.dim })
      for (const field of group.fields) {
        const value = (next ?? values)[field.key] as number
        row(ui, t(field.label), 96, () => {
          const moved = slider(ui, `gen:${String(field.key)}`, value, { min: field.min, max: field.max, step: field.step, width: width - 20 - 96 - 48 - 16 })
          if (moved !== value) next = { ...(next ?? values), [field.key]: moved }
          ui.box({ width: 48, direction: 'row', justify: 'end' }, () => ui.text(String(value)))
        })
      }
    })
  }
  return next
}

export interface Menu {
  draw(ui: Ui): void
  destroy(): void
}

/** Главное меню. play — начать игру (или редактор) с этим запуском. */
export function createMenu(play: (launch: Launch) => void): Menu {
  let screen: Screen = 'main'
  let saves = listSaves()
  const refresh = () => (saves = listSaves())
  const home = () => (screen = 'main')
  // Музыка звучит, пока открыто меню, и стихает, когда начинается игра.
  const music: MenuMusic = createMenuMusic()
  const preview: Preview = createPreview()
  let logo: HTMLImageElement | null = null
  if (typeof Image !== 'undefined') {
    logo = new Image()
    logo.src = `${import.meta.env.BASE_URL}favicon.png`
  }

  // Новая игра.
  const fresh = () => ({
    name: t('menu.gameName', { number: saves.length + 1 }),
    size: DEFAULT_SETTINGS.world.size,
    weather: { ...DEFAULT_WEATHER },
    generator: { ...DEFAULT_CONFIG, seed: randomSeed() } as GeneratorConfig,
    fine: false,
  })
  let game = fresh()
  // Новый мир.
  const freshWorld = () => ({ name: t('menu.mapName', { number: saves.length + 1 }), width: 256, height: 256, blank: false, generator: { ...DEFAULT_CONFIG, seed: randomSeed() } as GeneratorConfig, busy: false })
  let world = freshWorld()
  // Сохранения: ошибка, подтверждение удаления, переименование.
  let error = ''
  let confirming: string | null = null
  let renaming: { id: string; name: string } | null = null
  // Сеть.
  let server = loadServer()

  const open = (next: Screen) => {
    screen = next
    if (next === 'new') game = fresh()
    if (next === 'world') world = freshWorld()
    if (next === 'saves') {
      refresh()
      error = ''
      confirming = null
      renaming = null
    }
    if (next === 'network') server = loadServer()
  }

  /** Место окна: у широкого экрана — слева, у узкого — по центру; над окном — логотип. */
  const place = (ui: Ui, _width?: number) => {
    const top = Math.max(16, Math.min(120, ui.height * 0.12)) + 76
    return ui.width < NARROW ? { centerX: 0, top } : { left: 56, top }
  }

  const logoRow = (ui: Ui) => {
    const narrow = ui.width < NARROW
    const top = Math.max(16, Math.min(120, ui.height * 0.12))
    ui.box({ anchor: narrow ? { centerX: 0, top } : { left: 56, top }, width: narrow ? undefined : 300, direction: 'row', align: 'center', justify: 'center', gap: 12, passive: true }, () => {
      if (logo?.complete && logo.naturalWidth) ui.image(logo, 44, 44, { smooth: true })
      ui.box({ direction: 'row', gap: 8 }, () => {
        for (const letter of 'KHAROS') ui.text(letter, { font: 'logo', color: COLORS.bright, shadow: COLORS.ink, lift: 4 })
      })
    })
  }

  const join = (target: typeof server) => {
    try {
      localStorage.setItem(SERVER_KEY, JSON.stringify(target))
    } catch {
      // Адрес просто не запомнится.
    }
    play({ kind: 'server', ...target, name: cleanName(target.name) })
  }

  const mainWindow = (ui: Ui) => {
    const width = ui.width < NARROW ? Math.min(280, ui.width - 32) : 300
    const last = lastLaunch()
    const lastTitle = !last ? undefined : last.kind === 'save' ? `${last.slot.name} — ${playtime(last.slot.tick)}` : last.kind === 'server' ? t('menu.lastServer', { url: last.url }) : undefined
    menuWindow(ui, { id: 'menu:main', title: t('menu.title'), width, anchor: place(ui, width) }, () => {
      ui.box({ direction: 'column', align: 'stretch', gap: 6 }, () => {
        // Продолжать нечего — кнопки нет, а не мёртвая серая. Есть что — это и есть главное действие.
        if (last && menuButton(ui, 'menu:resume', t('common.resume'), { primary: true, tip: lastTitle })) play(last)
        if (menuButton(ui, 'menu:new', t('menu.newGame'))) open('new')
        if (menuButton(ui, 'menu:saves', t('menu.saves'))) open('saves')
        if (menuButton(ui, 'menu:network', t('menu.network'))) open('network')
        if (menuButton(ui, 'menu:settings', t('common.settings'))) open('settings')
        if (menuButton(ui, 'menu:about', t('menu.about'))) open('about')
      })
      link(ui, 'menu:github', 'GitHub', REPOSITORY, true)
    })
  }

  /** Подпись над полем — как .menu__field у страницы. */
  const above = (ui: Ui, label: string, control: () => void) =>
    ui.box({ direction: 'column', align: 'stretch', gap: 4 }, () => {
      ui.text(label, { color: COLORS.dim, wrap: true })
      control()
    })

  const newGameWindow = (ui: Ui) => {
    const width = Math.min(900, ui.width - 32)
    const narrow = ui.width < NARROW
    const inner = width - 36
    const aside = 328
    const form = narrow ? inner : inner - aside - 16
    const preset = PRESETS.find((option) => {
      const changes = generatorChanges(game.generator)
      return Object.keys(changes).length === Object.keys(option.generator).length && Object.entries(option.generator).every(([key, value]) => changes[key as keyof GeneratorConfig] === value)
    })
    let start = false
    const anchor = place(ui, width)
    const back = menuWindow(ui, { id: 'menu:new', title: t('menu.newGame'), width, anchor, back: t('common.back'), actions: () => {
      if (menuButton(ui, 'menu:new:start', t('menu.start'), { primary: true })) start = true
    } }, () => {
      const fields = () => ui.box({ direction: 'column', align: 'stretch', gap: 10, width: form }, () => {
        above(ui, t('menu.name'), () => {
          const name = textField(ui, 'menu:new:name', game.name, { maxLength: 40 })
          game.name = name.value
          if (name.keys.includes('Enter')) start = true
        })
        above(ui, t('menu.seed'), () => ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
          const seed = numberField(ui, 'menu:new:seed', game.generator.seed, {})
          if (seed !== null) game.generator = { ...game.generator, seed: Math.floor(seed) }
          if (menuButton(ui, 'menu:new:random', t('menu.random'), { tip: t('menu.randomTip') })) game.generator = { ...game.generator, seed: randomSeed() }
        }))
        above(ui, t('menu.mapSize'), () => {
          const size = chips(ui, 'menu:new:size', MAP_SIZES.map((option) => [option, String(option)] as const), game.size, form)
          if (size) game.size = size.picked
        })
        above(ui, t('menu.terrain'), () => {
          const picked = chips(ui, 'menu:new:preset', PRESETS.map((option) => [option, t(option.label)] as const), preset, form)
          if (picked) game.generator = { ...DEFAULT_CONFIG, ...picked.picked.generator, seed: game.generator.seed }
        })
        for (const knob of KNOBS) {
          const share = knobValue(knob, game.generator)
          row(ui, t(`menu.knobs.${knob.key}`), 150, () => {
            const moved = slider(ui, `menu:new:knob:${knob.key}`, share, { min: 0, max: 1, step: 0.01, width: form - 158 - 56 })
            if (moved !== share) game.generator = { ...game.generator, [knob.key]: Math.round((knob.from + moved * (knob.to - knob.from)) * 1000) / 1000 }
            ui.box({ width: 48, direction: 'row', justify: 'end' }, () => ui.text(String(Math.round(share * 100))))
          }, t(`menu.knobTips.${knob.key}`), `menu:new:knobLabel:${knob.key}`)
        }
        game.fine = details(ui, 'menu:new:fine', t('menu.fineTuning'), game.fine, () => {
          const tuned = generatorGroups(ui, game.generator, form)
          if (tuned) game.generator = tuned
        })
        above(ui, t('menu.dayLasts'), () => {
          const day = chips(ui, 'menu:new:day', DAY_LENGTHS.map((seconds) => [seconds, seconds ? t('menu.minutes', { minutes: seconds / 60 }) : t('menu.dayStands')] as const), game.weather.dayLength, form)
          if (day) game.weather = { ...game.weather, dayLength: day.picked }
        })
        row(ui, t('menu.startHour'), 150, () => {
          const hour = slider(ui, 'menu:new:hour', game.weather.startHour, { min: 0, max: 23.5, step: 0.5, width: form - 158 - 56 })
          if (hour !== game.weather.startHour) game.weather = { ...game.weather, startHour: hour }
          ui.box({ width: 48, direction: 'row', justify: 'end' }, () => ui.text(clock(game.weather.startHour)))
        })
        const changes = checkbox(ui, 'menu:new:weather', game.weather.changes, t('menu.weatherChanges'))
        if (changes !== game.weather.changes) game.weather = { ...game.weather, changes }
      })
      const side = () => previewBox(ui, game.generator, game.size, game.size, narrow ? inner : 320)
      ui.box({ direction: narrow ? 'column' : 'row', gap: 16, align: narrow ? 'center' : 'start' }, () => {
        if (narrow) {
          side()
          fields()
        } else {
          fields()
          side()
        }
      })
    })
    if (back) home()
    if (start) {
      const changes = generatorChanges(game.generator)
      play({ kind: 'save', slot: createSlot(game.name.trim() || t('menu.gameName', { number: saves.length + 1 }), game.size, game.generator.seed, game.weather, Object.keys(changes).length ? changes : undefined) })
    }
  }

  /** Схема карты с подписью размера и долей скал и болот. */
  const previewBox = (ui: Ui, config: GeneratorConfig, width: number, height: number, side: number) => {
    preview.show(config, width, height)
    ui.box({ direction: 'column', align: 'center', gap: 6 }, () => {
      ui.box({ skin: 'well', padding: 4, alpha: preview.ready ? undefined : 0.6 }, () => ui.image(preview.canvas, side, side, { live: !preview.ready }))
      const { shares } = preview
      ui.text(`${t('menu.previewSize', { width, height })}${shares ? ` · ${t('menu.previewShares', { rock: Math.round(shares.rock * 100), swamp: Math.round(shares.swamp * 100) })}` : ''}`, { color: COLORS.muted, wrap: side })
    })
  }

  const newWorldWindow = (ui: Ui) => {
    const width = Math.min(900, ui.width - 32)
    const narrow = ui.width < NARROW
    const inner = width - 36
    const form = narrow ? inner : inner - 328 - 16
    const side = (value: number) => Math.max(WORLD_MIN, Math.min(WORLD_MAX, Math.round(value) || WORLD_MIN))
    const create = async (edit: boolean) => {
      world.busy = true
      const config: GeneratorConfig = { ...world.generator, ...(world.blank && { blank: true }) }
      const changes = generatorChanges(config)
      const slot = createSlot(world.name.trim() || t('menu.mapName', { number: saves.length + 1 }), world.width, config.seed, undefined, Object.keys(changes).length ? changes : undefined, world.height)
      // В редактор — мир без игроков, сохранённый сразу; в игру — его заведёт воркер со стартовым набором.
      if (edit) await storeSave(slot.id, createSim({ generator: config, size: world.width, height: world.height }).save())
      play({ kind: edit ? 'editor' : 'save', slot: listSaves().find((other) => other.id === slot.id) ?? slot } as Launch)
    }
    const anchor = place(ui, width)
    const back = menuWindow(ui, { id: 'menu:world', title: t('menu.newMap'), width, anchor, back: t('common.back'), actions: () => {
      if (menuButton(ui, 'menu:world:play', t('common.play'), { disabled: world.busy })) void create(false)
      if (menuButton(ui, 'menu:world:edit', t('menu.toEditor'), { primary: true, disabled: world.busy })) void create(true)
    } }, () => {
      const fields = () => ui.box({ direction: 'column', align: 'stretch', gap: 10, width: form }, () => {
        above(ui, t('menu.name'), () => {
          world.name = textField(ui, 'menu:world:name', world.name, { maxLength: 40 }).value
        })
        above(ui, t('menu.sizeTiles'), () => ui.box({ direction: 'row', align: 'center', gap: 8 }, () => {
          const w = numberField(ui, 'menu:world:width', world.width, { width: 90, live: true })
          if (w !== null) world.width = side(w)
          ui.text('×')
          const h = numberField(ui, 'menu:world:height', world.height, { width: 90, live: true })
          if (h !== null) world.height = side(h)
        }))
        above(ui, t('menu.terrain'), () => {
          const kind = chips(ui, 'menu:world:blank', [[false, t('menu.generator')], [true, t('menu.blank')]] as const, world.blank, form, { tips: [undefined, t('menu.blankTip')] })
          if (kind) world.blank = kind.picked
        })
        if (!world.blank) {
          above(ui, t('menu.seed'), () => ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
            const seed = numberField(ui, 'menu:world:seed', world.generator.seed, {})
            if (seed !== null) world.generator = { ...world.generator, seed: Math.floor(seed) }
            if (menuButton(ui, 'menu:world:random', t('menu.random'))) world.generator = { ...world.generator, seed: randomSeed() }
          }))
          const tuned = generatorGroups(ui, world.generator, form)
          if (tuned) world.generator = tuned
        }
      })
      const config = { ...world.generator, ...(world.blank && { blank: true }) }
      ui.box({ direction: narrow ? 'column' : 'row', gap: 16, align: narrow ? 'center' : 'start' }, () => {
        if (narrow) previewBox(ui, config, world.width, world.height, inner)
        fields()
        if (!narrow) previewBox(ui, config, world.width, world.height, 320)
      })
    })
    if (back) {
      refresh()
      open('saves')
    }
  }

  const savesWindow = (ui: Ui) => {
    const width = Math.min(560, ui.width - 32)
    const inner = width - 36
    const anchor = place(ui, width)
    /** Открывает файл сохранения: edit — сразу в редакторе, не заводя слот, иначе — в новый слот. */
    const pick = (edit: boolean) =>
      void pickFile('.kharos').then(async (file) => {
        if (!file) return
        if (edit) return play({ kind: 'editor', file: file.bytes, name: file.name })
        try {
          await importSave(file.bytes)
          error = ''
        } catch (reason) {
          error = reason instanceof Error ? reason.message : String(reason)
        }
        refresh()
      })
    const back = menuWindow(ui, { id: 'menu:saves', title: t('menu.saves'), width, anchor, back: t('common.back'), actions: () => {
      if (menuButton(ui, 'menu:saves:load', t('menu.loadFile'))) pick(false)
      if (menuButton(ui, 'menu:saves:edit', t('menu.fileToEditor'), { tip: t('menu.fileToEditorTip') })) pick(true)
      // Главное — «Играть» у сохранения; без сохранений главное — начать новый мир.
      if (menuButton(ui, 'menu:saves:new', t('menu.newWorld'), { primary: !saves.length, tip: t('menu.newWorldTip') })) open('world')
    } }, () => {
      if (!saves.length) ui.text(t('menu.noSaves'), { color: COLORS.muted, wrap: true })
      for (const slot of saves) saveRow(ui, slot, inner)
      if (error) ui.text(error, { color: COLORS.bad, wrap: true })
    })
    if (back) home()
  }

  const saveRow = (ui: Ui, slot: SaveSlot, width: number) => {
    const id = `save:${slot.id}`
    ui.box({ skin: 'well', padding: 12, direction: 'column', gap: 8 }, () => {
      ui.box({ direction: 'column', gap: 2 }, () => {
        if (renaming?.id === slot.id) {
          const field = textField(ui, `${id}:name`, renaming.name, { width: width - 24, maxLength: 40, focus: true })
          renaming.name = field.value
          if (field.keys.includes('Enter') && renaming.name.trim()) {
            renameSave(slot.id, renaming.name.trim())
            renaming = null
            refresh()
          } else if (field.keys.includes('Escape') || field.keys.includes('Blur')) renaming = null
        } else ui.text(slot.name, { color: COLORS.bright, wrap: true })
        const size = slot.height && slot.height !== slot.size ? `${slot.size}×${slot.height}` : slot.size
        ui.text(`${date(slot.updated)} · ${playtime(slot.tick)} · ${t('menu.mapOf', { size })} · ${t('menu.seedOf', { seed: slot.seed })}`, { font: 'small', color: COLORS.muted, wrap: true })
      })
      ui.box({ direction: 'row', gap: 6, wrap: true }, () => {
        if (menuButton(ui, `${id}:play`, t('common.play'), { primary: true })) play({ kind: 'save', slot })
        if (menuButton(ui, `${id}:editor`, t('menu.editor'), { disabled: slot.updated === slot.created, tip: t('menu.editorTip') })) play({ kind: 'editor', slot })
        if (menuButton(ui, `${id}:rename`, t('menu.rename'))) renaming = { id: slot.id, name: slot.name }
        if (menuButton(ui, `${id}:file`, t('menu.file'), { tip: t('menu.fileTip') })) {
          void exportSave(slot.id).then((file) => {
            if (file) download(`${slot.name}.kharos`, file)
            else error = t('menu.notSavedYet')
          })
        }
        if (confirming === slot.id) {
          if (menuButton(ui, `${id}:sure`, t('menu.sure'), { danger: true })) {
            deleteSave(slot.id)
            confirming = null
            refresh()
          }
        } else if (menuButton(ui, `${id}:remove`, t('menu.remove'))) confirming = slot.id
      })
    })
  }

  const networkWindow = (ui: Ui) => {
    const width = ui.width < NARROW ? Math.min(280, ui.width - 32) : 300
    const anchor = place(ui, width)
    const valid = /^wss?:\/\/./.test(server.url)
    let connect = false
    const back = menuWindow(ui, { id: 'menu:network', title: t('menu.network'), width, anchor, back: t('common.back'), actions: () => {
      if (menuButton(ui, 'menu:network:connect', t('common.connect'), { primary: true, disabled: !valid })) connect = true
    } }, () => {
      const recent = recentServers()
      if (recent.length) {
        ui.text(t('menu.recentServers'), { color: COLORS.dim })
        for (const item of recent) {
          ui.box({ skin: 'well', padding: 12, direction: 'column', gap: 6 }, () => {
            ui.text(item.url, { color: COLORS.bright, wrap: true })
            ui.text(`${date(item.played)}${item.name ? ` · ${item.name}` : ''}`, { font: 'small', color: COLORS.muted, wrap: true })
            ui.box({ direction: 'row', gap: 6 }, () => {
              if (menuButton(ui, `recent:${item.url}:play`, t('common.play'), { primary: true })) join({ ...server, url: item.url, name: server.name || item.name || '' })
              if (menuButton(ui, `recent:${item.url}:forget`, '×', { tip: t('menu.forget') })) forgetServer(item.url)
            })
          })
        }
      }
      ui.text(t('menu.nick'), { color: COLORS.dim })
      const nick = textField(ui, 'menu:network:nick', server.name, { maxLength: NAME_LENGTH, placeholder: t('menu.nickHint') })
      server = { ...server, name: nick.value }
      ui.text(t('menu.serverAddress'), { color: COLORS.dim })
      let url: InputField = { value: server.url, focused: false, keys: [] }
      ui.box({ direction: 'row', align: 'center', gap: 6 }, () => {
        url = textField(ui, 'menu:network:url', server.url, { placeholder: 'ws://host:port' })
        server = { ...server, url: url.value.trim() }
        // «Этот компьютер» подставляет адрес — и стоит рядом с ним, а не среди действий окна.
        if (menuButton(ui, 'menu:network:local', t('menu.thisComputer'))) server = { ...server, url: localServerUrl() }
      })
      ui.box({ id: 'menu:network:lagLabel' }, () => ui.text(t('menu.lag'), { color: COLORS.dim }))
      ui.tip('menu:network:lagLabel', t('menu.lagTip'))
      const lag = numberField(ui, 'menu:network:lag', server.lag, { min: 0 })
      if (lag !== null) server = { ...server, lag }
      if (isInsecure(server.url)) ui.text(t('errors.insecure'), { color: COLORS.warn, wrap: true })
      ui.text(t('menu.sharedWorld'), { color: COLORS.muted, wrap: true })
      if ((nick.keys.includes('Enter') || url.keys.includes('Enter')) && valid) connect = true
    })
    if (back) home()
    if (connect && valid) join(server)
  }

  const aboutWindow = (ui: Ui) => {
    const width = ui.width < NARROW ? Math.min(280, ui.width - 32) : 300
    const anchor = place(ui, width)
    const back = menuWindow(ui, { id: 'menu:about', title: t('menu.about'), width, anchor, back: t('common.back') }, () => {
      ui.text(t('menu.aboutText'), { wrap: true })
      ui.text(t('menu.authors').toUpperCase(), { color: COLORS.muted })
      for (const author of AUTHORS) link(ui, `about:${author.name}`, author.name, author.url)
      ui.text(t('menu.sourceCode').toUpperCase(), { color: COLORS.muted })
      link(ui, 'about:repo', 'github.com/vicimpa/kharos', REPOSITORY)
      ui.text(t('menu.protocolVersion', { version: PROTOCOL_VERSION }), { color: COLORS.muted })
    })
    if (back) home()
  }

  return {
    draw(ui) {
      logoRow(ui)
      if (screen === 'main') mainWindow(ui)
      else if (screen === 'new') newGameWindow(ui)
      else if (screen === 'world') newWorldWindow(ui)
      else if (screen === 'saves') savesWindow(ui)
      else if (screen === 'network') networkWindow(ui)
      else if (screen === 'about') aboutWindow(ui)
      else if (screen === 'settings') {
        const width = ui.width < NARROW ? Math.min(280, ui.width - 32) : 300
        if (settingsWindow(ui, () => music.refresh(), place(ui, width))) home()
      }
    },
    destroy() {
      music.destroy()
      preview.destroy()
    },
  }
}
