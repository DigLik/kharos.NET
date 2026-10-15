// Точка входа нативной игры: собранная игра в рантайме окна. Переписанного здесь нет — это тот же путь, что у
// витрины главного меню (src/ui/Showcase.tsx): крошечный мир считает сам рантайм, без хоста, сценка сама ведёт своё
// действие, а рисуют те же проходы. Отличия от витрины четыре, и все оттого, что оболочка ещё не всё умеет (см. README):
// - графику рисует drawArt прямо здесь: воркера в оболочке нет, и игра идёт тем же путём, что у себя на странице,
//   когда воркер не запустился (см. loadArt.ts);
// - проходы собираются одним куском, без экрана загрузки (warmUp.ts): очередь видеокарты у окна своя, а не общая на
//   все вкладки браузера;
// - ни управления, ни звука, ни интерфейса: это следующие шаги оболочки;
// - в конце — проверка, что нарисованное дошло до драйвера: та же, что у проверки фундамента (см. README).

import { createGame, simOptions, type Game } from '../../src/game/game'
import { loadArt } from '../../src/game/loadArt'
import { stage } from '../../src/game/stage'
import { DEFAULT_SETTINGS, type MapSettings } from '../../src/map/settings'
import { LOCAL_PLAYER } from '../../src/net/local'
import { SCENES, createSim, type Scene, type SceneName } from '../../src/sim'

/** Что окно сказало до начала игры (см. `game_options` в `src/main.rs`): сценка, час суток и ровные ли часы. */
type Options = { fixed?: boolean; scene?: string | null; hour?: number | null }
const native = globalThis as typeof globalThis & { __kharos_options?: Options; __kharos_roll?: () => void }
const shell: Options = native.__kharos_options ?? {}

/** Сценка, которую ведёт окно: у неё своё действие, и ввода для него не нужно. Другую выбирает `KHAROS_SCENE`. */
const SCENE: SceneName = shell.scene && shell.scene in SCENES ? (shell.scene as SceneName) : 'mining'
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
  const read = (milliseconds: number) => {
    since ??= milliseconds
    if (++frames < CHECK_FRAME) {
      requestAnimationFrame(read)
      return
    }
    const gl = canvas.getContext('webgl2')
    if (!gl) return
    const pixel = new Uint8Array(4)
    const width = gl.drawingBufferWidth
    const height = gl.drawingBufferHeight
    gl.readPixels(width >> 1, height >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel)
    // Ход кадров — за последние CHECK_FRAME кадров, и печатается раз в столько же: журнал не тонет в строках.
    const pace = Math.round(frames / ((milliseconds - since) / 1000))
    frames = 0
    since = milliseconds
    console.log(`центр кадра: ${pixel[0]}, ${pixel[1]}, ${pixel[2]} (окно ${width}×${height}, ${pace} кадров/с)`)
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

/** Начало: графика рисуется один раз, а дальше идёт игра. Ход рисования виден в журнале — это несколько секунд. */
async function main() {
  let told = -10
  await loadArt((share) => {
    const percent = Math.round(share * 100)
    if (percent < told + 10) return
    told = percent
    console.log(`графика: ${percent}%`)
  })
  start(SCENE)
}

void main().catch((error: unknown) => console.error('игра не запустилась:', error))
