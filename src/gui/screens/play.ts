// Экран игры на холсте: сама игра, интерфейс игрока поверх неё (hud.ts) и окна экрана — подключение к серверу,
// пароль, ошибка, поражение, меню паузы с настройками; в живом редакторе мира — его панель и рамки камер игроков.
// Перенесено со страницы (src/ui/GameView.tsx).

import { createGame, type Game } from '../../game/game'
import type { HudState } from '../../game/hud'
import { stage } from '../../game/stage'
import { t } from '@kharos/i18n'
import type { MapSettings } from '@kharos/shared/settings'
import { PasswordRequired } from '../../net/connect'
import type { Replica } from '../../net/replica'
import { startSession, type PlayLaunch } from '../../ui/launch'
import { barHeight } from '../bar'
import { menuButton, menuWindow, textField } from '../form'
import { createHud, topHeight } from '../hud'
import { links } from '../links'
import type { Gui, Ui } from '../ui'
import { COLORS } from '../widgets'
import { EDITOR_WIDTH, createEditorPanel, type EditorPanel } from './editorPanel'
import { settingsWindow } from './settings'
import type { Screen, ScreenOverlay } from './screen'

/** Экран игры: launch — во что играть, exit — выйти в главное меню. */
export function createPlay(launch: PlayLaunch, settings: MapSettings, gui: Gui, overlay: ScreenOverlay, exit: () => void): Screen {
  let game: Game | null = null
  let error: unknown = null
  /** Настоящая игра — за свою базу: в ней можно сдаться и проиграть. Песочница и показательный бой — не в счёт. */
  const real = launch.kind === 'save' || launch.kind === 'server'
  /** Сервер просит пароль: wrong — введённый не подошёл. Окно спрашивает снова, пока не пустят или не отменят. */
  let asking: { wrong: boolean } | null = null
  let password = ''
  let connecting = launch.kind === 'server'
  /** Открыто меню поверх игры. Игра за ним идёт дальше; управление выключено, звук тише. */
  let paused = false
  let settingsOpen = false
  /** «Сдаться» нажато один раз: второе нажатие подтверждает. */
  let surrendering = false
  /** Панель живого редактора мира (/editor): есть, пока хост держит игрока в редакторе. */
  let editor: EditorPanel | null = null
  let closed = false
  const abort = new AbortController()
  /** Последнее состояние игры: по нему экран отвечает, сколько с краёв закрыто панелями. */
  let lastState: HudState | null = null

  // Интерфейс игрока — раньше игры: его клавиши должны идти раньше клавиш управления (Esc закрывает окна).
  const hud = createHud(() => game!, gui)
  const pause = (on: boolean) => {
    paused = on
    settingsOpen = false
    surrendering = false
    if (game) game.paused = on
  }
  links.openMenu = () => pause(true)

  // Пока открыто меню, горячие клавиши панели и игры молчат; Escape его закрывает.
  const onKey = (event: KeyboardEvent) => {
    if (!paused || gui.typing) return
    event.stopImmediatePropagation()
    if (event.code === 'Escape') pause(false)
  }
  window.addEventListener('keydown', onKey, true)

  /** Игра остановилась на ошибке: экран её показывает, а журнал хранит стек — по нему видно, где упало. */
  const stopped = (reason: unknown) => {
    console.error('игра остановилась:', reason instanceof Error ? (reason.stack ?? reason.message) : reason)
    error = reason
  }

  const attempt = (secret?: string) => {
    connecting = launch.kind === 'server'
    startSession(launch.kind === 'server' && secret !== undefined ? { ...launch, password: secret } : launch, settings, abort.signal)
      .then((session) => {
        connecting = false
        if (closed) return session.sim.destroy()
        game = createGame(stage(), settings, stopped, session, { slot: launch.kind === 'save' ? launch.slot.id : undefined, overlay })
        game.onMenu = () => pause(true)
      })
      .catch((reason: unknown) => {
        if (closed) return
        connecting = false
        if (reason instanceof PasswordRequired) asking = { wrong: reason.wrong }
        else error = reason
      })
  }
  attempt()

  /** Окно посреди затемнённого экрана: подключение, пароль, ошибка. */
  const notice = (ui: Ui, title: string, body: () => void, actions: () => void) => {
    ui.box({ anchor: { left: 0, right: 0, top: 0, bottom: 0 }, background: 'rgb(8 17 28 / 0.88)', direction: 'column', align: 'center', justify: 'center', gap: 12, padding: 24 }, () => {
      ui.text(title, { color: COLORS.text })
      body()
      ui.box({ direction: 'row', gap: 8 }, actions)
    })
  }

  /** Рамки камер других игроков в редакторе: что у каждого на экране, с ником. */
  const playerViews = (ui: Ui, current: Game) => {
    const views = (current.scene.sim as Partial<Replica>).views ?? []
    for (const view of views) {
      const from = current.scene.camera.tileToScreen(view.left, view.top)
      const to = current.scene.camera.tileToScreen(view.right, view.bottom)
      ui.box({ anchor: { left: from.x, top: from.y - 18 }, direction: 'column', passive: true }, () => {
        ui.box({ background: 'rgb(0 0 0 / 0.6)', padding: [1, 6] }, () => ui.text(view.name, { color: '#ffe066' }))
        ui.box({ width: Math.max(4, to.x - from.x), height: Math.max(4, to.y - from.y), outline: '#ffe066' })
      })
    }
    return views.length > 0
  }

  let animate = false
  return {
    get animate() {
      return animate
    },
    insets(width) {
      if (!game) return { top: 0, bottom: 0, right: 0 }
      const state = lastState
      const live = !!state?.editor
      return { top: topHeight(width), bottom: live ? 0 : barHeight(width), right: live ? EDITOR_WIDTH + 16 : 0 }
    },
    draw(ui) {
      animate = false
      lastState = null
      if (error !== null) {
        notice(ui, t('game.stopped'), () => ui.text(error instanceof Error ? error.message : String(error), { color: COLORS.soft, wrap: Math.min(720, ui.width - 48) }), () => {
          if (menuButton(ui, 'play:menu', t('common.toMenu'))) exit()
          if (menuButton(ui, 'play:reload', launch.kind === 'server' ? t('game.reconnect') : t('game.reload'))) location.reload()
        })
        return
      }
      if (asking) {
        let submit = false
        notice(ui, asking.wrong ? t('game.wrongPassword') : t('game.passwordNeeded'), () => {
          const field = textField(ui, 'play:password', password, { width: 240, password: true, placeholder: t('game.password'), focus: true })
          password = field.value
          if (field.keys.includes('Enter') && password) submit = true
        }, () => {
          if (menuButton(ui, 'play:cancel', t('common.cancel'))) exit()
          if (menuButton(ui, 'play:connect', t('common.connect'), { disabled: !password })) submit = true
        })
        if (submit) {
          asking = null
          attempt(password)
        }
        return
      }
      if (connecting && launch.kind === 'server') {
        notice(ui, t('game.connecting'), () => ui.text(launch.url, { color: COLORS.soft }), () => {
          if (menuButton(ui, 'play:abort', t('common.cancel'))) exit()
        })
        return
      }
      if (!game) return
      const state = game.hud()
      lastState = state
      hud.draw(ui, state)
      // Живой редактор мира: хост держит игрока в нём, пока тот не наберёт /editor ещё раз.
      if (state.editor) {
        const current = game
        editor ??= createEditorPanel({
          sim: current.scene.sim,
          game: current,
          remote: true,
          // Ответ хоста приходит в чат: здесь правка всегда «ушла».
          apply: (edit) => void current.edit(edit),
          names: () => new Map(((current.scene.sim as Partial<Replica>).players ?? []).map(({ player, name }) => [player, name])),
        })
        animate = playerViews(ui, current)
        editor.draw(ui, t('editor.worldTitle'), launch.kind === 'server' ? t('editor.onServer') : t('editor.liveWorld'), topHeight(ui.width) + 8, () => {
          if (menuButton(ui, 'editor:leave', t('editor.leave'))) current.say('/editor')
        })
      } else if (editor) {
        editor.destroy()
        editor = null
      }
      if (real && state.defeated && !state.editor) {
        ui.box({ anchor: { centerX: 0, top: 56 }, skin: 'panel', padding: [20, 28], direction: 'column', align: 'center', gap: 10 }, () => {
          ui.text(t('game.defeat'), { font: 'title', color: COLORS.bad })
          ui.text(launch.kind === 'server' ? t('game.defeatServer') : t('game.defeatLocal'), { wrap: 360 })
          ui.box({ direction: 'row', gap: 8 }, () => {
            if (menuButton(ui, 'play:defeat:menu', t('common.toMenu'))) exit()
            if (menuButton(ui, 'play:defeat:again', t('game.startOver'))) game?.respawn()
          })
        })
      }
      if (paused) pauseMenu(ui, state.defeated)
    },
    destroy() {
      closed = true
      abort.abort()
      window.removeEventListener('keydown', onKey, true)
      editor?.destroy()
      hud.destroy()
      game?.destroy()
      game = null
      links.openMenu = () => {}
    },
  }

  /** Меню поверх игры: продолжить, начать заново (бой и песочница), настройки, сдаться, выйти. */
  function pauseMenu(ui: Ui, defeated: boolean) {
    ui.box({ anchor: { left: 0, right: 0, top: 0, bottom: 0 }, background: 'rgb(8 17 28 / 0.6)' })
    if (settingsOpen) {
      if (settingsWindow(ui, () => game?.refreshSound())) settingsOpen = false
      return
    }
    // Кнопки — списком через 6, как в главном меню.
    menuWindow(ui, { id: 'pause', title: t('game.menu'), width: Math.min(300, ui.width - 32) }, () => ui.box({ direction: 'column', align: 'stretch', gap: 6 }, () => {
      if (menuButton(ui, 'pause:resume', t('common.resume'), { primary: true })) pause(false)
      if (launch.kind === 'battle' || launch.kind === 'sandbox') {
        if (menuButton(ui, 'pause:restart', launch.kind === 'battle' ? t('game.newBattle') : t('game.restart'))) {
          game?.restart()
          pause(false)
        }
      }
      if (menuButton(ui, 'pause:settings', t('common.settings'))) settingsOpen = true
      if (real && !defeated) {
        // Сдаться — дважды: первое нажатие спрашивает, второе подтверждает.
        if (menuButton(ui, 'pause:surrender', surrendering ? `${t('game.surrender')}?` : t('game.surrender'), { danger: true, tip: surrendering ? t('game.surrenderConfirm') : undefined })) {
          if (surrendering) {
            game?.send({ type: 'surrender' })
            pause(false)
          } else surrendering = true
        }
      }
      if (menuButton(ui, 'pause:exit', launch.kind === 'save' ? t('game.saveAndExit') : t('game.exitToMenu'))) exit()
    }))
  }
}
