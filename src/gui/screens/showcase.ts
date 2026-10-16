// Фон главного меню: слайды-сценки — добыча, стройка, оборона, бои — на каждый раз новой крошечной местности, в
// случайный час и свою погоду. Мир считает сама вкладка, без хоста и тумана войны; камера сама следит за
// происходящим. Сценка, которая не встала на этой местности или кончилась раньше времени, сменяется следующей. Если
// отрисовка не запустилась (нет WebGL 2), фон просто остаётся тёмным. Перенесено со страницы (src/ui/Showcase.tsx).

import { createGame, simOptions, type Game } from '../../game/game'
import { stage } from '../../game/stage'
import type { MapSettings } from '../../map/settings'
import { LOCAL_PLAYER } from '../../net/local'
import { SCENES, createSim, type SceneName } from '@kharos/sim-ts'
import type { Ui } from '../ui'
import type { ScreenOverlay } from './screen'

/** Сколько длится слайд и затемнение между слайдами, в миллисекундах. */
const SLIDE_TIME = 40000
const FADE_TIME = 1200
/** Пока местность первого кадра собирается, экран тёмный. */
const SETTLE_TIME = 700
/** Как часто сценка ведёт свой мир, в миллисекундах. */
const DRIVE_INTERVAL = 500
/** Карта витрины крошечная: её считают с нуля для каждого слайда, прямо во вкладке. */
const SHOWCASE_SIZE = 128
const NIGHT = '8 17 28'

const NAMES = Object.keys(SCENES) as SceneName[]

/** Погода слайда: случайный час, чаще днём, и быстрые сутки — за слайд заметно смещается свет. */
const randomWeather = () => ({ startHour: Math.random() < 0.75 ? 7 + Math.random() * 11 : Math.random() * 24, dayLength: 600, changes: true })

/** Отладка: ?scene=<имя> в адресной строке показывает только эту сценку. */
const FORCED = NAMES.find((name) => name === new URLSearchParams(location.search).get('scene'))

/** Следующая сценка — случайная, но не та же, что сейчас. */
const nextScene = (current?: SceneName) => {
  if (FORCED) return FORCED
  const choices = NAMES.filter((name) => name !== current)
  return choices[Math.floor(Math.random() * choices.length)]
}

export interface Showcase {
  /** Затемнение: тёмное, пока слайд собирается и сменяется. Рисуется первым — под меню. */
  draw(ui: Ui): void
  /** Затемнение меняется: кадр интерфейса нужен каждый кадр. */
  readonly animate: boolean
  destroy(): void
}

export function createShowcase(settings: MapSettings, overlay: ScreenOverlay): Showcase {
  let game: Game | null = null
  let shown = false
  /** Когда сменилось затемнение и какой была его прозрачность тогда: оно идёт к новой за FADE_TIME. */
  let changed = performance.now()
  let from = 1
  let veil = 1
  let scene: SceneName = nextScene()
  let timers: ReturnType<typeof setTimeout>[] = []
  let drive: ReturnType<typeof setInterval> | undefined
  let destroyed = false

  const show = (on: boolean) => {
    if (on === shown) return
    from = veil
    changed = performance.now()
    shown = on
  }
  const later = (action: () => void, delay: number) => timers.push(setTimeout(action, delay))
  const clear = () => {
    timers.forEach(clearTimeout)
    timers = []
    clearInterval(drive)
    game?.destroy()
    game = null
  }
  const next = () => {
    show(false)
    later(() => slide(nextScene(scene)), FADE_TIME)
  }

  const slide = (name: SceneName) => {
    if (destroyed) return
    clear()
    scene = name
    const local: MapSettings = {
      ...settings,
      generator: { ...settings.generator, seed: Math.floor(Math.random() * 2 ** 31) },
      world: { ...settings.world, size: SHOWCASE_SIZE },
    }
    const options = simOptions(local)
    // В сценках нет сетей, где пополнить магазины: юниты стреляют бесплатно, см. Rules.unitAmmo.
    const sim = createSim({ ...options, rules: { ...options.rules, unitAmmo: false }, fog: false, weather: randomWeather() })
    const made = SCENES[name]()
    if (!made.create(sim, LOCAL_PLAYER)) {
      sim.destroy()
      later(() => slide(nextScene(name)), 0)
      return
    }
    try {
      game = createGame(stage(), local, () => show(false), { sim, player: LOCAL_PLAYER }, { showcase: true, overlay })
    } catch {
      sim.destroy()
      return
    }
    later(() => show(true), SETTLE_TIME)
    let ended = false
    drive = setInterval(() => {
      if (ended || !made.drive) return
      if (made.drive(sim, LOCAL_PLAYER, DRIVE_INTERVAL / 1000)) return
      // Кончилась — пусть доиграет пару секунд и сменяется.
      ended = true
      later(next, 2500)
    }, DRIVE_INTERVAL)
    later(next, SLIDE_TIME - FADE_TIME)
  }
  slide(scene)

  return {
    get animate() {
      return performance.now() - changed < FADE_TIME
    },
    draw(ui) {
      const share = Math.min(1, (ui.now - changed) / FADE_TIME)
      veil = from + ((shown ? 0.25 : 1) - from) * share
      // Ступенями по 1/16: плавнее не разглядеть, а каждая новая — перерисовка холста.
      ui.box({ anchor: { left: 0, right: 0, top: 0, bottom: 0 }, background: `rgb(${NIGHT} / ${Math.round(veil * 16) / 16})`, passive: true })
    },
    destroy() {
      destroyed = true
      clear()
    },
  }
}
