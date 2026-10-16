// Приложение на холсте (пункт 10.4 плана): загрузка, главное меню над витриной, игра, редактор — всё рисует интерфейс
// на холсте (ui.ts) поверх одного холста игры. Перенесено со страницы (src/ui/App.tsx и экраны): DOM у приложения —
// только сам холст, и тем же путём оно идёт в нативной оболочке.
//
// Интерфейс один на всё приложение: его кадр строится здесь, по вводу или раз в FRAME_MS, а в кадр игры его кладёт
// проход интерфейса (pass.ts) — у игры, витрины и страницы местности он последний. Пока игры нет (экран загрузки,
// окна ошибок до начала), интерфейс кладёт в кадр само приложение.

import { markActivated } from '../audio/audio'
import { loadSounds } from '../audio/loadSounds'
import { loadArt } from '../game/loadArt'
import { stage } from '../game/stage'
import { warmUp } from '../game/warmUp'
import { createContext } from '../gl'
import { t } from '@kharos/i18n'
import { DEFAULT_SETTINGS } from '../map/settings'
import { graphics } from '../render/graphics'
import { lastDrawn } from '../render/renderer'
import { launchFromAddress, recallLaunch, rememberLast, rememberLaunch, type Launch } from '../ui/launch'
import { menuButton } from './form'
import { createGuiPass } from './pass'
import { createEditor } from './screens/editor'
import { createMenu } from './screens/menu'
import { createPlay } from './screens/play'
import type { Screen, ScreenOverlay } from './screens/screen'
import { createShowcase } from './screens/showcase'
import { createTerrainTest, isTerrainTest } from './screens/terrain'
import { createGui, type Ui } from './ui'
import { COLORS } from './widgets'

/** Как часто строится кадр интерфейса без ввода, в миллисекундах: панели берут состояние игры опросом. */
const FRAME_MS = 100
/** Доли общей работы загрузки: звук синтезируется примерно вдесятеро дольше, чем рисуется графика. */
const ART_SHARE = 0.08
const GPU_SHARE = 0.04
const SOUND_SHARE = 0.88
/** Сколько мс после кадра игры приложение не кладёт интерфейс в кадр само: игра рисует его сама. */
const DRAWN_RECENTLY = 250
const NIGHT = [8 / 255, 17 / 255, 28 / 255]

/** Пикселей холста на пиксель CSS — как считает renderer.resize: не плотнее двух и с настройкой разрешения. */
const pixelDensity = () => Math.min(globalThis.devicePixelRatio || 1, 2) * graphics().resolution

export interface AppOptions {
  /** С чего начать после загрузки: игра или редактор, минуя меню (нативная оболочка, адресная строка). */
  launch?: Launch
  /** Без экрана загрузки с кнопкой «Продолжить»: у окна нет запрета на звук до первого нажатия. */
  skipContinue?: boolean
}

/** Запускает приложение в элементе root: холст во всё окно и интерфейс на нём. */
export function startApp(root: HTMLElement, options: AppOptions = {}) {
  const canvas = stage()
  Object.assign(canvas.style, { display: 'block', width: '100%', height: '100%' })
  // У нативной оболочки страницы нет: холст и так во всё окно.
  root.appendChild?.(canvas)
  const gui = createGui(canvas)
  let changed = true
  let screen: Screen | null = null
  let built = -Infinity

  /** Холст интерфейса для игры: залить в текстуру, если поменялся, и не отдавать миру нажатия по панелям. */
  const overlay: ScreenOverlay = {
    surface: gui.surface,
    takeChanged() {
      const was = changed
      changed = false
      return was
    },
    covers: (x, y) => gui.covers(x, y),
    insets: (width, height) => screen?.insets?.(width, height) ?? { top: 0, bottom: 0, right: 0 },
  }

  // Приложение кладёт интерфейс в кадр само, пока его не рисует игра: свой контекст того же холста и свой проход.
  let present: { gl: WebGL2RenderingContext; pass: ReturnType<typeof createGuiPass> } | null = null
  let presented = -Infinity
  const presentSelf = (now: number) => {
    if (!present) {
      try {
        const gl = createContext(canvas)
        present = { gl, pass: createGuiPass(gl, overlay) }
      } catch {
        return
      }
    }
    // Перерыв — кадр рисовала игра и забирала заливку себе: текстура приложения могла устареть.
    if (now - presented > DRAWN_RECENTLY) changed = true
    presented = now
    const { gl, pass } = present
    const density = pixelDensity()
    const width = Math.max(1, Math.round(canvas.clientWidth * density))
    const height = Math.max(1, Math.round(canvas.clientHeight * density))
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width
      canvas.height = height
    }
    // Прямо на холст, а не bindScreen: прогрев в это время подменяет экран целью (drawOffscreen).
    gl.bindFramebuffer(gl.FRAMEBUFFER, null)
    gl.viewport(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight)
    gl.clearColor(NIGHT[0], NIGHT[1], NIGHT[2], 1)
    gl.clear(gl.COLOR_BUFFER_BIT)
    // Проходу интерфейса кадр не нужен: он кладёт холст во весь экран.
    pass.draw({} as never)
  }

  const show = (next: Screen | null) => {
    screen?.destroy()
    screen = next
    built = -Infinity
  }

  // Экран загрузки: графика и звук готовятся в воркерах; готово — «Продолжить» (браузер не даёт звуку играть, пока
  // на странице ничего не нажали, а так и музыка меню, и игра звучат сразу).
  let share = 0
  let ready = false
  const loading: Screen = {
    draw(ui: Ui) {
      ui.box({ anchor: { left: 0, right: 0, top: 0, bottom: 0 }, background: '#08111c', direction: 'column', align: 'center', justify: 'center', gap: 16, padding: [0, 16] }, () => {
        ui.text('Kharos', { font: 'logo', color: COLORS.bright, shadow: COLORS.ink, lift: 2 })
        const width = Math.min(320, ui.width - 32)
        ui.box({ skin: 'well', width, height: 24, padding: 4, direction: 'row' }, () => {
          // Заполнение — полосами по 8 пикселей, как индикаторы на зданиях.
          const fill = Math.floor((width - 8) * share)
          ui.box({ direction: 'row', gap: 2 }, () => {
            for (let x = 0; x < fill; x += 8) ui.box({ width: Math.min(6, fill - x), height: 16, background: COLORS.teamMid })
          })
        })
        if (ready) {
          if (menuButton(ui, 'loading:proceed', t('common.resume'), { primary: true, width: 160, height: 40 })) proceed()
        } else ui.box({ height: 40, direction: 'row', align: 'center' }, () => ui.text(t('game.loadingAssets', { percent: Math.floor(share * 100) }), { color: COLORS.soft, shadow: COLORS.ink }))
      })
    },
    destroy() {},
  }
  // Продолжить можно и с клавиатуры: Enter или пробел.
  const onLoadingKey = (event: KeyboardEvent) => {
    if (screen === loading && ready && (event.code === 'Enter' || event.code === 'Space')) proceed()
  }
  window.addEventListener('keydown', onLoadingKey)

  /** Главное меню над витриной. */
  const menu = (): Screen => {
    const showcase = createShowcase(DEFAULT_SETTINGS, overlay)
    const main = createMenu(open)
    return {
      get animate() {
        return showcase.animate
      },
      draw(ui) {
        showcase.draw(ui)
        main.draw(ui)
      },
      destroy() {
        main.destroy()
        showcase.destroy()
      },
    }
  }

  /** Открывает игру, редактор или — без запуска — меню. Перезагрузка страницы возвращает туда же. */
  function open(launch: Launch | null) {
    rememberLaunch(launch)
    if (launch) rememberLast(launch)
    const exit = () => {
      // Из игры, открытой адресной строкой, меню возвращает без параметров: иначе перезагрузка снова откроет её.
      if (typeof history !== 'undefined' && location.search) history.replaceState(null, '', location.pathname)
      open(null)
    }
    if (!launch) show(menu())
    else if (launch.kind === 'editor') show(createEditor(launch, DEFAULT_SETTINGS, overlay, exit))
    else show(createPlay(launch, DEFAULT_SETTINGS, gui, overlay, exit))
  }

  function proceed() {
    if (screen !== loading) return
    window.removeEventListener('keydown', onLoadingKey)
    markActivated()
    open(options.launch ?? launchFromAddress() ?? recallLaunch())
  }

  if (isTerrainTest()) show(createTerrainTest(overlay))
  else {
    show(loading)
    let art = 0
    let gpu = 0
    let sound = 0
    const progress = () => (share = art * ART_SHARE + gpu * GPU_SHARE + sound * SOUND_SHARE)
    void Promise.all([
      loadArt((value) => ((art = value), progress())).then((drawn) => warmUp(drawn, (value) => ((gpu = value), progress()))),
      loadSounds((value) => ((sound = value), progress())),
    ]).then(() => {
      // Доли — оценка: какая-то часть могла не дослать последнюю. Готово — значит полоса полная.
      share = 1
      ready = true
      if (options.skipContinue) proceed()
    })
  }

  // Кадр интерфейса — по вводу, раз в FRAME_MS, каждый кадр, если на экране что-то движется, и сразу, как поменялся
  // размер окна: иначе до срока игра растягивала бы старый холст интерфейса. Пока кадр не рисует игра, приложение
  // кладёт интерфейс в кадр само.
  let size = ''
  const tick = (now: number) => {
    requestAnimationFrame(tick)
    if (!screen) return
    const width = canvas.clientWidth || globalThis.innerWidth || 1
    const height = canvas.clientHeight || globalThis.innerHeight || 1
    const resized = size !== `${width}x${height}`
    if (resized || gui.touched || screen.animate || now - built >= FRAME_MS || screen === loading) {
      built = now
      size = `${width}x${height}`
      // Плотность — та же, что холст получит в этом кадре (renderer.resize): холст ещё может быть старого размера.
      const density = pixelDensity()
      if (gui.frame(width, height, density, (ui) => screen!.draw(ui))) changed = true
    }
    // Экран загрузки кладёт в кадр само приложение всегда: игры ещё нет, а пробный кадр прогрева идёт мимо холста.
    if (screen === loading || performance.now() - lastDrawn() > DRAWN_RECENTLY) presentSelf(now)
  }
  requestAnimationFrame(tick)
}
