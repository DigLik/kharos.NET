import { createContext, drawOffscreen } from '../gl'
import { DEFAULT_SETTINGS, type MapSettings } from '@kharos/shared/settings'
import { LOCAL_PLAYER } from '../net/local'
import { uploadAtlasSlowly, uploadSteps } from '../render/atlas'
import { createRenderer } from '../render/renderer'
import { createSim } from '@kharos/sim-ts'
import { artAtlases, type Art } from './art'
import { createScene, finish, simOptions, stagePasses } from './game'
import { stage } from './stage'

/** Сколько шагов в создании проходов, см. stagePasses. Нужно только полосе загрузки: ошибётся — полоса чуть соврёт. */
const PASS_STEPS = 15
/**
 * Дольше этого, в миллисекундах, шаг не ждёт, пока видеокарта доделает прошлый: драйвер, который о готовности
 * молчит, загрузку не остановит.
 */
const STEP_TIMEOUT = 3000
/** Сколько ждать кадра, в миллисекундах: вкладку могли скрыть уже после запроса, и кадр не придёт, пока её не покажут. */
const FRAME_TIMEOUT = 250
/** Мир, на котором создаются проходы: крошечный, как у витрины меню. */
const WARM_SIZE = 128

/** Следующий кадр браузера. В скрытой вкладке кадров нет, а таймеры замедлены — там ждём лишь хода очереди событий. */
function nextFrame() {
  return new Promise<void>((resolve) => {
    if (document.hidden) {
      const { port1, port2 } = new MessageChannel()
      port1.onmessage = () => resolve()
      port2.postMessage(0)
      return
    }
    const timer = setTimeout(resolve, FRAME_TIMEOUT)
    requestAnimationFrame(() => {
      clearTimeout(timer)
      resolve()
    })
  })
}

/**
 * Ждёт следующего кадра и того, чтобы видеокарта выполнила всё отправленное: в её очереди не копится больше одной
 * порции работы. Очередь у всех вкладок общая, и заваленная, она останавливает весь браузер.
 */
async function settle(gl: WebGL2RenderingContext) {
  const sync = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0)
  gl.flush()
  const start = performance.now()
  do await nextFrame()
  while (sync && !gl.isContextLost() && gl.clientWaitSync(sync, 0, 0) === gl.TIMEOUT_EXPIRED && performance.now() - start < STEP_TIMEOUT)
  gl.deleteSync(sync)
}

let warming: Promise<void> | null = null
const listeners = new Set<(share: number) => void>()

/**
 * Готовит видеокарту к игре, пока идёт экран загрузки: заливает атласы полосами и собирает шейдеры по одному
 * проходу за кадр. Всё это общее на контекст (см. shared.ts в gl), а контекст на странице один (stage.ts) — меню
 * и игра потом берут готовое. Разом те же 57 МБ пикселей и два десятка шейдеров на слабой видеокарте вешают
 * браузер целиком. onProgress получает долю готового. Не вышло (нет WebGL 2, контекст отобрали) — не беда: игра
 * создаст всё сама и о настоящей ошибке скажет на своём экране. Делается один раз за жизнь страницы; повторный
 * вызов ждёт того же.
 */
export function warmUp(art: Art, onProgress?: (share: number) => void, settings: MapSettings = DEFAULT_SETTINGS): Promise<void> {
  if (onProgress) listeners.add(onProgress)
  warming ??= prepare(art, (share) => listeners.forEach((listener) => listener(share)), settings)
  return warming.finally(() => onProgress && listeners.delete(onProgress))
}

async function prepare(art: Art, onProgress: (share: number) => void, settings: MapSettings) {
  try {
    const canvas = stage()
    const gl = createContext(canvas)
    const atlases = artAtlases(art)
    const total = atlases.reduce((sum, atlas) => sum + uploadSteps(atlas), 0) + PASS_STEPS
    let done = 0
    /** Делает работу по шагу за кадр. */
    const pace = async <T>(steps: Generator<void, T>) => {
      let step = steps.next()
      while (!step.done) {
        if (gl.isContextLost()) throw new Error('Контекст потерян')
        onProgress(Math.min(1, ++done / total))
        await settle(gl)
        step = steps.next()
      }
      return step.value
    }
    for (const atlas of atlases) await pace(uploadAtlasSlowly(gl, atlas))

    const local: MapSettings = { ...settings, world: { ...settings.world, size: WARM_SIZE } }
    const sim = createSim({ ...simOptions(local), fog: false })
    try {
      const scene = createScene(sim, LOCAL_PLAYER, local)
      const built = await pace(stagePasses(gl, scene, art, false))
      for (const pass of built.passes) pass.destroy()
      built.landWindow.destroy()

      // Первый кадр — тоже работа видеокарты: цели отрисовки создаются, шейдеры впервые исполняются. На холсте в
      // это время экран загрузки: кадр (и чистка за ним в renderer.destroy) идёт мимо него, в цель в один пиксель.
      let landWindow = built.landWindow
      const renderer = createRenderer(canvas, (context) => {
        const next = finish(stagePasses(context, scene, art, false))
        landWindow = next.landWindow
        return next.passes
      })
      await drawOffscreen(gl, async () => {
        try {
          const { width, height } = renderer.resize()
          scene.camera.width = width
          scene.camera.height = height
          landWindow.update(sim.land, scene.camera, width, height)
          renderer.draw(scene.camera, 0, 0)
          await settle(gl)
        } finally {
          renderer.destroy()
          landWindow.destroy()
        }
      })
    } finally {
      sim.destroy()
    }
  } catch {
    // См. описание: игра справится сама.
  }
  onProgress(1)
}
