import { useEffect, useRef, useState } from 'preact/hooks'
import { createGame, type Game } from '../game/game'
import { stage } from '../game/stage'
import { Stage } from './Stage'
import type { HudState } from '../game/hud'
import type { MapSettings } from '../map/settings'
import { EditorPanel } from './EditorPanel'
import type { Replica } from '../net/replica'
import { Hud } from './Hud'
import { Settings } from './Menu'
import { PasswordRequired } from '../net/connect'
import { startSession, type PlayLaunch } from './launch'
import { t } from '../i18n'

/** Как часто интерфейс сверяется с игрой, в миллисекундах. */
const HUD_INTERVAL = 100

interface GameViewProps {
  launch: PlayLaunch
  settings: MapSettings
  /** Выйти в главное меню. */
  exit(): void
}

/** Экран игры: холст, на котором живёт сама игра, и интерфейс поверх него. */
export function GameView({ launch, settings, exit }: GameViewProps) {
  const gameRef = useRef<Game | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [hud, setHud] = useState<HudState | null>(null)
  /** Настоящая игра — за свою базу: в ней можно сдаться и проиграть. Песочница и показательный бой — не в счёт. */
  const real = launch.kind === 'save' || launch.kind === 'server'

  /** Сервер просит пароль: wrong — введённый не подошёл. Окно спрашивает снова, пока не пустят или не отменят. */
  const [asking, setAsking] = useState<{ wrong: boolean } | null>(null)
  const [password, setPassword] = useState('')
  const connectRef = useRef<(password?: string) => void>(() => {})
  /** Открыто меню поверх игры. Игра за ним идёт дальше; управление выключено, звук тише. */
  const [paused, setPaused] = useState(false)
  /** Настройки, открытые из меню игры. */
  const [settingsOpen, setSettingsOpen] = useState(false)
  useEffect(() => {
    setSettingsOpen(false)
    if (gameRef.current) gameRef.current.paused = paused
    if (!paused) return
    // Горячие клавиши панели и игры молчат, пока открыто меню; Escape его закрывает.
    const close = (event: KeyboardEvent) => {
      if (event.code !== 'Escape' && event.target instanceof HTMLInputElement) return
      event.stopImmediatePropagation()
      if (event.code === 'Escape') setPaused(false)
    }
    window.addEventListener('keydown', close, true)
    return () => window.removeEventListener('keydown', close, true)
  }, [paused])
  /** Идёт подключение к серверу: окно с отменой, пока сервер не ответит. */
  const [connecting, setConnecting] = useState(launch.kind === 'server')

  // Игра создаётся один раз.
  useEffect(() => {
    let closed = false
    // Уход с экрана обрывает и подключение: сокет к мёртвому серверу не висит в фоне.
    const abort = new AbortController()
    const start = async (password?: string) => {
      setConnecting(launch.kind === 'server')
      const session = await startSession(launch.kind === 'server' && password !== undefined ? { ...launch, password } : launch, settings, abort.signal)
      setConnecting(false)
      if (closed) return session.sim.destroy()
      gameRef.current = createGame(stage(), settings, setError, session, { slot: launch.kind === 'save' ? launch.slot.id : undefined })
      gameRef.current.onMenu = () => setPaused(true)
    }
    const attempt = (password?: string) =>
      start(password).catch((error: unknown) => {
        if (closed) return
        setConnecting(false)
        if (error instanceof PasswordRequired) setAsking({ wrong: error.wrong })
        else setError(error)
      })
    connectRef.current = attempt
    attempt()
    return () => {
      closed = true
      abort.abort()
      gameRef.current?.destroy()
      gameRef.current = null
    }
  }, [])

  // Интерфейс не подписывается на каждую сущность, а раз в HUD_INTERVAL спрашивает у игры готовое состояние
  // и перерисовывается, только если оно изменилось.
  useEffect(() => {
    let last = ''
    const timer = setInterval(() => {
      const game = gameRef.current
      if (!game) return
      const next = game.hud()
      const key = JSON.stringify(next)
      if (key === last) return
      last = key
      setHud(next)
    }, HUD_INTERVAL)
    return () => clearInterval(timer)
  }, [])

  return (
    <main class="game">
      <Stage name="game__canvas" />
      {hud && error === null && gameRef.current && (
        <Hud
          state={hud}
          send={(command) => gameRef.current?.send(command)}
          place={(building) => gameRef.current?.place(building)}
          pave={(tool) => gameRef.current?.pave(tool)}
          zones={(shown) => gameRef.current?.zones(shown)}
          route={(start) => gameRef.current?.route(start)}
          serve={(start) => gameRef.current?.serve(start)}
          carry={(pick) => gameRef.current?.carry(pick)}
          truckMenu={(building) => gameRef.current?.truckMenu(building)}
          patrol={(start) => gameRef.current?.patrol(start)}
          aim={(silo) => gameRef.current?.aim(silo)}
          deploy={(mcv) => gameRef.current?.deploy(mcv)}
          minimap={gameRef.current.minimap}
          lookAt={(x, y) => gameRef.current?.lookAt(x, y)}
          flyTo={(x, y) => gameRef.current?.flyTo(x, y)}
          lookAtSelection={() => gameRef.current?.lookAtSelection()}
          selectGroup={(digit, add) => gameRef.current?.selectGroup(digit, add)}
          assignGroup={(digit) => gameRef.current?.assignGroup(digit)}
          clearGroup={(digit) => gameRef.current?.clearGroup(digit)}
          cancelMode={() => gameRef.current?.cancelMode()}
          finishMode={() => gameRef.current?.finishMode()}
          narrow={(type, remove) => gameRef.current?.narrow(type, remove)}
          moveSelected={(x, y) => gameRef.current?.moveSelected(x, y)}
          say={(text) => gameRef.current?.say(text)}
          menu={
            <button data-tip={t('game.menuTip')} onClick={() => setPaused(true)}>
              {t('game.menu')}
            </button>
          }
        />
      )}
      {hud?.editor && error === null && gameRef.current && <PlayerViews game={gameRef.current} />}
      {hud?.editor && error === null && gameRef.current && (
        <EditorPanel
          sim={gameRef.current.scene.sim}
          game={gameRef.current}
          remote
          // Ответ хоста приходит в чат: здесь правка всегда «ушла».
          apply={(edit) => void gameRef.current?.edit(edit)}
          title={t('editor.worldTitle')}
          subtitle={launch.kind === 'server' ? t('editor.onServer') : t('editor.liveWorld')}
          names={new Map(((gameRef.current.scene.sim as Partial<Replica>).players ?? []).map(({ player, name }) => [player, name]))}
        >
          <button onClick={() => gameRef.current?.say('/editor')}>{t('editor.leave')}</button>
        </EditorPanel>
      )}
      {paused && hud && error === null && (
        <div class="menu game__pause">
          {settingsOpen ? (
            <Settings back={() => setSettingsOpen(false)} onSound={() => gameRef.current?.refreshSound()} />
          ) : (
            <section class="menu__window">
              <h2 class="menu__title">{t('game.menu')}</h2>
              <nav class="menu__list">
                <button class="menu__primary" autoFocus onClick={() => setPaused(false)}>
                  {t('common.resume')}
                </button>
                {(launch.kind === 'battle' || launch.kind === 'sandbox') && (
                  <button
                    onClick={() => {
                      gameRef.current?.restart()
                      setPaused(false)
                    }}
                  >
                    {launch.kind === 'battle' ? t('game.newBattle') : t('game.restart')}
                  </button>
                )}
                <button onClick={() => setSettingsOpen(true)}>{t('common.settings')}</button>
                {real && !hud.defeated && (
                  <button
                    class="menu__danger"
                    onClick={() => {
                      if (!confirm(t('game.surrenderConfirm'))) return
                      gameRef.current?.send({ type: 'surrender' })
                      setPaused(false)
                    }}
                  >
                    {t('game.surrender')}
                  </button>
                )}
                <button onClick={exit}>{launch.kind === 'save' ? t('game.saveAndExit') : t('game.exitToMenu')}</button>
              </nav>
            </section>
          )}
        </div>
      )}
      {real && hud?.defeated && !hud.editor && error === null && (
        <div class="game__defeat" role="alert">
          <strong>{t('game.defeat')}</strong>
          <span>{launch.kind === 'server' ? t('game.defeatServer') : t('game.defeatLocal')}</span>
          <div class="game__actions">
            <button onClick={exit}>{t('common.toMenu')}</button>
            <button onClick={() => gameRef.current?.respawn()}>{t('game.startOver')}</button>
          </div>
        </div>
      )}
      {connecting && launch.kind === 'server' && error === null && (
        <div class="game__error" role="status">
          <strong>{t('game.connecting')}</strong>
          <span>{launch.url}</span>
          <div class="game__actions">
            <button onClick={exit}>{t('common.cancel')}</button>
          </div>
        </div>
      )}
      {asking && error === null && (
        <form
          class="game__error"
          onSubmit={(event) => {
            event.preventDefault()
            setAsking(null)
            connectRef.current(password)
          }}
        >
          <strong>{asking.wrong ? t('game.wrongPassword') : t('game.passwordNeeded')}</strong>
          <input class="game__password" type="password" autoFocus value={password} placeholder={t('game.password')} onInput={(event) => setPassword(event.currentTarget.value)} />
          <div class="game__actions">
            <button type="button" onClick={exit}>
              {t('common.cancel')}
            </button>
            <button type="submit" disabled={!password}>
              {t('common.connect')}
            </button>
          </div>
        </form>
      )}
      {error !== null && (
        <div class="game__error" role="alert">
          <strong>{t('game.stopped')}</strong>
          <pre>{error instanceof Error ? error.message : String(error)}</pre>
          <div class="game__actions">
            <button onClick={exit}>{t('common.toMenu')}</button>
            <button onClick={() => location.reload()}>{launch.kind === 'server' ? t('game.reconnect') : t('game.reload')}</button>
          </div>
        </div>
      )}
    </main>
  )
}

/**
 * Камеры других игроков в редакторе: рамка того, что у каждого на экране, с ником. Двигается каждый кадр вместе со
 * своей камерой, а не по перерисовке интерфейса.
 */
function PlayerViews({ game }: { game: Game }) {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    let frame = requestAnimationFrame(function draw() {
      const root = ref.current
      const views = (game.scene.sim as Partial<Replica>).views ?? []
      if (root) {
        while (root.children.length < views.length) root.appendChild(Object.assign(document.createElement('div'), { className: 'player-view' }))
        while (root.children.length > views.length) root.lastChild!.remove()
        views.forEach((view, i) => {
          const box = root.children[i] as HTMLDivElement
          const from = game.scene.camera.tileToScreen(view.left, view.top)
          const to = game.scene.camera.tileToScreen(view.right, view.bottom)
          Object.assign(box.style, { left: `${from.x}px`, top: `${from.y}px`, width: `${to.x - from.x}px`, height: `${to.y - from.y}px` })
          box.dataset.name = view.name
        })
      }
      frame = requestAnimationFrame(draw)
    })
    return () => cancelAnimationFrame(frame)
  }, [game])
  return <div ref={ref} class="player-views" />
}
