// Точка входа нативной игры: собранная игра в рантайме окна. Переписанного здесь нет: без переменных окружения окно
// запускает то же приложение, что страница (src/gui/app.ts), — загрузку, главное меню над витриной, игру, редактор;
// весь интерфейс рисуется на холсте. KHAROS_MODE открывает игру сразу, минуя меню. KHAROS_SCENE — сценка витрины без
// интерфейса, для роликов: крошечный мир считает сам рантайм, без хоста, сценка сама ведёт своё действие.
// В конце — проверка, что нарисованное дошло до драйвера: та же, что у проверки фундамента (см. README).

import { createGame, simOptions, type Game } from '../../src/game/game'
import { loadArt } from '../../src/game/loadArt'
import { stage } from '../../src/game/stage'
import { DEFAULT_SETTINGS, type MapSettings } from '@kharos/shared/settings'
import { createSlot } from '../../src/game/storage'
import { startApp } from '../../src/gui/app'
import { LOCAL_PLAYER, type GameMode } from '@kharos/host/local'
import { lastDrawn } from '../../src/render/renderer'
import { SCENES, createSim, type Scene, type SceneName } from '@kharos/sim-ts'
import { loadLang, preferredLang } from '@kharos/i18n'
import type { Launch } from '../../src/ui/launch'

/** Что окно сказало до начала игры (см. `game_options` в `src/main.rs`): сценка, час суток и ровные ли часы. */
type Options = { fixed?: boolean; scene?: string | null; hour?: number | null; mode?: string | null }
const native = globalThis as typeof globalThis & { __kharos_options?: Options; __kharos_roll?: () => void }
const shell: Options = native.__kharos_options ?? {}

/** Сценка без интерфейса (`KHAROS_SCENE`): у неё своё действие, и ввода для него не нужно. */
const SCENE: SceneName | null = shell.scene && shell.scene in SCENES ? (shell.scene as SceneName) : null
/**
 * Во что играть сразу, минуя меню (`KHAROS_MODE`): новая игра, тестовая карта с готовой базой или показательный бой —
 * как в меню страницы. Мир считает локальный хост, а камерой и приказами управляют мышь и клавиши.
 */
const MODES: GameMode[] = ['play', 'sandbox', 'battle']
const MODE = MODES.find((mode) => mode === shell.mode) ?? null

/** Карта крошечная: её считает сам рантайм, без хоста, — как у витрины меню. */
const SIZE = 128
/** Как часто сценка ведёт свой мир: тот же срок, что у витрины меню. */
const DRIVE_SECONDS = 0.5
/** Сколько раз пробовать местность, если сценка на ней не встала: сценке подходит не всякая. */
const ATTEMPTS = 8
/** Через сколько кадров читается цвет с экрана: за это время проходы рисуют и местность, и юнитов. */
const CHECK_FRAME = 120

let sim: ReturnType<typeof createSim> | null = null
let game: Game | null = null
let drive: number | undefined

/**
 * Погода мира: случайный час, чаще днём, и короткие сутки — за время показа свет заметно смещается. Для ролика час
 * задаётся (`KHAROS_HOUR`): свет в кадре не должен зависеть от случая.
 */
const weather = () => ({
  startHour: shell.hour ?? (Math.random() < 0.75 ? 7 + Math.random() * 11 : Math.random() * 24),
  dayLength: 600,
  changes: true,
})

/** Ошибка внутри кадра: игра останавливается, а окно остаётся — видно, на чём остановилось. */
const fail = (error: unknown) => console.error('кадр не нарисовался:', error)

/**
 * Проверка, что кадр дошёл до драйвера: цвет в середине окна читается с экрана. Читает его игра, а считают проходы
 * игры, поэтому по числам видно, что нарисовано что-то настоящее, а не пустая рамка. Дело это кадровое: после показа
 * буфера драйвер его содержимое помнить не обязан, а в кадре обёртка зовёт работу по очереди — сперва кадр игры, потом
 * эту проверку (см. `__kharos_frame`). Тут же виден и ход кадров: в оболочке графику рисует главный поток, и по числу
 * заметно, чего это стоит (воркера в оболочке нет).
 */
function checkFrame(canvas: HTMLCanvasElement) {
  let frames = 0
  let since: number | undefined
  let checked = false
  const read = (milliseconds: number) => {
    since ??= milliseconds
    if (++frames < CHECK_FRAME) {
      requestAnimationFrame(read)
      return
    }
    const gl = canvas.getContext('webgl2')
    if (!gl) return
    const width = gl.drawingBufferWidth
    const height = gl.drawingBufferHeight
    // Ход кадров — за последние CHECK_FRAME кадров, и печатается раз в столько же: журнал не тонет в строках.
    const pace = Math.round(frames / ((milliseconds - since) / 1000))
    frames = 0
    since = milliseconds
    // Цвет читается один раз: чтение с драйвера ждёт, пока видеокарта дорисует всё отправленное, и каждый раз
    // стоило бы рывка в кадре. Дальше в журнале только ход кадров.
    if (checked) {
      console.log(`ход кадров: ${pace} кадров/с (окно ${width}×${height})`)
    } else {
      const pixel = new Uint8Array(4)
      gl.readPixels(width >> 1, height >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel)
      console.log(`центр кадра: ${pixel[0]}, ${pixel[1]}, ${pixel[2]} (окно ${width}×${height}, ${pace} кадров/с)`)
      checked = true
    }
    requestAnimationFrame(read)
  }
  requestAnimationFrame(read)
}

/** Останавливает игру: прежний мир и его проходы дальше не нужны. */
function stop() {
  clearInterval(drive)
  drive = undefined
  game?.destroy()
  game = null
  sim?.destroy()
  sim = null
}

/**
 * Заводит мир со сценкой и пускает её. Попыток несколько: сценке подходит не всякая местность, и мир, на котором она
 * не встала, сразу уступает следующему — с новым зерном.
 */
function start(name: SceneName) {
  stop()
  const scene: Scene = SCENES[name]()
  for (let attempt = 0; attempt < ATTEMPTS; attempt++) {
    const settings: MapSettings = {
      ...DEFAULT_SETTINGS,
      generator: { ...DEFAULT_SETTINGS.generator, seed: Math.floor(Math.random() * 2 ** 31) },
      world: { ...DEFAULT_SETTINGS.world, size: SIZE },
    }
    const options = simOptions(settings)
    // В сценках нет сетей, где пополнить магазины: юниты стреляют бесплатно, см. Rules.unitAmmo.
    const world = createSim({ ...options, rules: { ...options.rules, unitAmmo: false }, fog: false, weather: weather() })
    if (!scene.create(world, LOCAL_PLAYER)) {
      world.destroy()
      continue
    }
    sim = world
    console.log(`нативная игра: сценка «${name}», карта ${SIZE}×${SIZE}`)
    const canvas = stage()
    game = createGame(canvas, settings, fail, { sim: world, player: LOCAL_PLAYER }, { showcase: true })
    checkFrame(canvas)
    // Мир встал и рисуется — запись (если она идёт) может начинать ролик.
    native.__kharos_roll?.()
    drive = window.setInterval(() => {
      if (!sim || !scene.drive) return
      // Сценка кончилась — на её место берётся такая же, с новым миром: показать больше нечего.
      if (!scene.drive(sim, LOCAL_PLAYER, DRIVE_SECONDS)) start(name)
    }, DRIVE_SECONDS * 1000)
    return
  }
  throw new Error(`сценка «${name}» не встала ни на одной из ${ATTEMPTS} местностей`)
}

/** Запуск по KHAROS_MODE: новая игра — новый слот, как её заводит меню; бой и песочница — как есть. */
function launchOf(mode: GameMode): Launch {
  if (mode !== 'play') return { kind: mode }
  const seed = Math.floor(Math.random() * 2 ** 31)
  return { kind: 'save', slot: createSlot('Native', DEFAULT_SETTINGS.world.size, seed) }
}

/** Начало: сценка без интерфейса — или приложение целиком. Ход рисования графики виден в журнале. */
async function main() {
  // Словарь — как у страницы: язык из окружения окна (navigator.languages берёт его из LANG, см. game_options).
  await loadLang(preferredLang())
  if (SCENE) {
    let told = -10
    await loadArt((share) => {
      const percent = Math.round(share * 100)
      if (percent < told + 10) return
      told = percent
      console.log(`графика: ${percent}%`)
    })
    start(SCENE)
    return
  }
  // У окна нет запрета на звук до первого нажатия: «Продолжить» после загрузки не нужен.
  startApp(document.body as HTMLElement, { launch: MODE ? launchOf(MODE) : undefined, skipContinue: true })
  console.log(MODE ? `нативная игра: приложение, сразу «${MODE}»` : 'нативная игра: приложение, главное меню')
  // Запись начинается с первого кадра игры (или витрины меню): экран загрузки в ролик не попадает.
  const rolled = setInterval(() => {
    if (lastDrawn() === -Infinity) return
    clearInterval(rolled)
    native.__kharos_roll?.()
  }, 100)
  checkFrame(stage())
}

void main().catch((error: unknown) => console.error('игра не запустилась:', error))
