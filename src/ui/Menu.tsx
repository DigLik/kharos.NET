import type { ComponentChildren } from 'preact'
import { useEffect, useRef, useState } from 'preact/hooks'
import { CHANNELS, loadLevel, loadMuted, loadVolume, storeLevel, storeMuted, storeVolume, type Channel } from '../audio/audio'
import { createMenuMusic, type MenuMusic } from '../audio/music'
import { createSlot, deleteSave, exportSave, importSave, listSaves, renameSave, storeSave, type SaveSlot } from '../game/storage'
import { DEFAULT_SETTINGS } from '../map/settings'
import { DEFAULT_CONFIG, type GeneratorConfig } from '../map/terrain'
import { DEFAULT_WEATHER, createSim } from '../sim'
import { GENERATOR_GROUPS, Groups } from './GeneratorFields'
import { NAME_LENGTH, PROTOCOL_VERSION, cleanName } from '../net/protocol'
import { isInsecure } from '../net/connect'
import { LANGS, chooseLang, currentLang, t, type Key } from '../i18n'
import { forgetServer, lastLaunch, localServerUrl, recentServers, type Launch } from './launch'
import { MapPreview } from './MapPreview'

/** Симуляция идёт 20 тиков в секунду. */
const TICKS_PER_SECOND = 20
/** Какой стороны бывает карта новой игры, в тайлах. */
const MAP_SIZES = [512, 1024, 2048]
/** Сколько длятся сутки новой игры, в секундах; 0 — время стоит. */
const DAY_LENGTHS = [600, 1200, 2400, 0]
const clock = (hour: number) => `${String(Math.floor(hour)).padStart(2, '0')}:${String(Math.floor((hour % 1) * 60)).padStart(2, '0')}`
const SERVER_KEY = 'kharos.server'

type Screen = 'main' | 'new' | 'saves' | 'network' | 'settings' | 'about' | 'world'

/** Страница проекта. */
const REPOSITORY = 'https://github.com/vicimpa/kharos'
/** Кто делает игру: имя и ссылка на профиль. */
const AUTHORS = [{ name: 'vicimpa', url: 'https://github.com/vicimpa' }]

interface MenuProps {
  play(launch: Launch): void
}

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

/** Отдаёт браузеру файл на скачивание. */
export function download(name: string, data: Uint8Array) {
  const link = document.createElement('a')
  link.href = URL.createObjectURL(new Blob([data as BlobPart], { type: 'application/octet-stream' }))
  link.download = name
  link.click()
  URL.revokeObjectURL(link.href)
}

/**
 * Окно меню: заголовок, содержимое и подвал — «Назад» слева, действия экрана справа, главное из них последним.
 * Так на всех экранах кнопки стоят на одних и тех же местах.
 */
function Window({ title, back, actions, children, wide }: { title: string; back?: () => void; actions?: ComponentChildren; children: ComponentChildren; wide?: boolean }) {
  return (
    <section class={wide ? 'menu__window menu__window--wide' : 'menu__window'}>
      <h2 class="menu__title">{title}</h2>
      {children}
      {(back || actions) && (
        <div class="menu__footer">
          {back && <button onClick={back}>{t('common.back')}</button>}
          {actions && <span class="menu__actions">{actions}</span>}
        </div>
      )}
    </section>
  )
}

/**
 * Главное меню поверх слайдов симуляции: продолжить последнюю игру, начать новую, загрузить сохранение, подключиться
 * к серверу, настроить игру.
 */
export function Menu({ play }: MenuProps) {
  const [screen, setScreen] = useState<Screen>('main')
  const [saves, setSaves] = useState(listSaves)
  const refresh = () => setSaves(listSaves())
  const home = () => setScreen('main')
  // Считается заново при каждом показе: слот могли удалить в «Сохранениях».
  const last = lastLaunch()
  const lastTitle = !last ? undefined : last.kind === 'save' ? `${last.slot.name} — ${playtime(last.slot.tick)}` : last.kind === 'server' ? t('menu.lastServer', { url: last.url }) : undefined
  // Музыка звучит, пока открыто меню, и стихает, когда начинается игра.
  const music = useRef<MenuMusic | null>(null)
  useEffect(() => {
    music.current = createMenuMusic()
    return () => music.current?.destroy()
  }, [])

  return (
    <div class="menu">
      <h1 class="menu__logo">
        <img src={`${import.meta.env.BASE_URL}favicon.svg`} alt="" />
        KHAROS
      </h1>
      {screen === 'main' && (
        <Window title={t('menu.title')}>
          <nav class="menu__list">
            {/* Продолжать нечего — кнопки нет, а не мёртвая серая. Есть что — это и есть главное действие. */}
            {last && (
              <button class="menu__primary" title={lastTitle} onClick={() => play(last)}>
                {t('common.resume')}
              </button>
            )}
            <button onClick={() => setScreen('new')}>{t('menu.newGame')}</button>
            <button
              onClick={() => {
                refresh()
                setScreen('saves')
              }}
            >
              {t('menu.saves')}
            </button>
            <button onClick={() => setScreen('network')}>{t('menu.network')}</button>
            <button onClick={() => setScreen('settings')}>{t('common.settings')}</button>
            <button onClick={() => setScreen('about')}>{t('menu.about')}</button>
          </nav>
          <a class="menu__link" href={REPOSITORY} target="_blank" rel="noopener noreferrer">
            GitHub
          </a>
        </Window>
      )}
      {screen === 'new' && <NewGame back={home} play={play} count={saves.length} />}
      {screen === 'saves' && <Saves back={home} saves={saves} refresh={refresh} play={play} create={() => setScreen('world')} />}
      {screen === 'world' && <NewWorld back={() => (refresh(), setScreen('saves'))} play={play} count={saves.length} />}
      {screen === 'network' && <Network back={home} play={play} />}
      {screen === 'settings' && <Settings back={home} onSound={() => music.current?.refresh()} />}
      {screen === 'about' && <About back={home} />}
    </div>
  )
}

/** Ползунок местности для человека: t от 0 до 1 переводится в параметр генератора и обратно. */
interface Knob {
  key: 'rockLevel' | 'swampLevel' | 'zoneScale' | 'zoneWarp' | 'biomeScale' | 'peakChance'
  from: number
  to: number
}

const KNOBS: Knob[] = [
  // Чем ниже уровень скал, тем их больше: ползунок «больше» вправо.
  {
    key: 'rockLevel',
    from: 0.75,
    to: 0.45,
  },
  {
    key: 'swampLevel',
    from: 0.2,
    to: 0.5,
  },
  {
    key: 'zoneScale',
    from: 24,
    to: 120,
  },
  {
    key: 'zoneWarp',
    from: 0,
    to: 80,
  },
  {
    key: 'biomeScale',
    from: 80,
    to: 600,
  },
  {
    key: 'peakChance',
    from: 0,
    to: 1,
  },
]

/** Готовые местности: отличия от генератора по умолчанию. */
const PRESETS: { label: Key; generator: Partial<GeneratorConfig> }[] = [
  { label: 'menu.presets.normal', generator: {} },
  {
    label: 'menu.presets.rocky',
    generator: { rockLevel: 0.5, swampLevel: 0.3, peakChance: 0.5 },
  },
  {
    label: 'menu.presets.desert',
    generator: { rockLevel: 0.68, swampLevel: 0.25, zoneScale: 80 },
  },
  {
    label: 'menu.presets.swamps',
    generator: { rockLevel: 0.62, swampLevel: 0.46, zoneWarp: 50 },
  },
  {
    label: 'menu.presets.islands',
    generator: { rockLevel: 0.6, swampLevel: 0.5, zoneScale: 40, zoneWarp: 10 },
  },
]

const knobValue = (knob: Knob, config: GeneratorConfig) => Math.max(0, Math.min(1, ((config[knob.key] as number) - knob.from) / (knob.to - knob.from)))

/** Отличия местности от генератора по умолчанию, без зерна: их и хранит слот. */
function generatorChanges(config: GeneratorConfig) {
  const changes: Partial<GeneratorConfig> = {}
  for (const key of Object.keys(DEFAULT_CONFIG) as (keyof GeneratorConfig)[]) if (key !== 'seed' && config[key] !== DEFAULT_CONFIG[key]) Object.assign(changes, { [key]: config[key] })
  return changes
}

function NewGame({ back, play, count }: { back(): void; play(launch: Launch): void; count: number }) {
  const [name, setName] = useState(() => t('menu.gameName', { number: count + 1 }))
  const [size, setSize] = useState(DEFAULT_SETTINGS.world.size)
  const [weather, setWeather] = useState(DEFAULT_WEATHER)
  const [generator, setGenerator] = useState<GeneratorConfig>(() => ({
    ...DEFAULT_CONFIG,
    seed: randomSeed(),
  }))
  const setSeed = (seed: number) => setGenerator({ ...generator, seed })
  const preset = PRESETS.find((preset) => {
    const changes = generatorChanges(generator)
    return Object.keys(changes).length === Object.keys(preset.generator).length && Object.entries(preset.generator).every(([key, value]) => changes[key as keyof GeneratorConfig] === value)
  })
  return (
    <Window
      title={t('menu.newGame')}
      back={back}
      wide
      actions={
        <button type="submit" form="new-game" class="menu__primary">
          {t('menu.start')}
        </button>
      }
    >
      <form
        id="new-game"
        class="menu__setup"
        onSubmit={(event) => {
          event.preventDefault()
          const changes = generatorChanges(generator)
          play({
            kind: 'save',
            slot: createSlot(name.trim() || t('menu.gameName', { number: count + 1 }), size, generator.seed, weather, Object.keys(changes).length ? changes : undefined),
          })
        }}
      >
        <div class="menu__form">
          <label class="menu__field">
            <span>{t('menu.name')}</span>
            <input value={name} maxLength={40} onInput={(event) => setName(event.currentTarget.value)} />
          </label>
          <label class="menu__field">
            <span>{t('menu.seed')}</span>
            <span class="menu__inline">
              <input type="number" value={generator.seed} onInput={(event) => setSeed(Math.floor(Number(event.currentTarget.value)) || 0)} />
              <button type="button" title={t('menu.randomTip')} onClick={() => setSeed(randomSeed())}>
                {t('menu.random')}
              </button>
            </span>
          </label>
          <div class="menu__field">
            <span>{t('menu.mapSize')}</span>
            <span class="menu__inline">
              {MAP_SIZES.map((option) => (
                <button type="button" key={option} class={option === size ? 'is-active' : undefined} onClick={() => setSize(option)}>
                  {option}
                </button>
              ))}
            </span>
          </div>
          <div class="menu__field">
            <span>{t('menu.terrain')}</span>
            <span class="menu__inline">
              {PRESETS.map((option) => (
                <button
                  type="button"
                  key={option.label}
                  class={option === preset ? 'is-active' : undefined}
                  onClick={() =>
                    setGenerator({
                      ...DEFAULT_CONFIG,
                      ...option.generator,
                      seed: generator.seed,
                    })
                  }
                >
                  {t(option.label)}
                </button>
              ))}
            </span>
          </div>
          {KNOBS.map((knob) => (
            <label key={knob.key} class="menu__field menu__field--slider" title={t(`menu.knobTips.${knob.key}`)}>
              <span>{t(`menu.knobs.${knob.key}`)}</span>
              <input
                type="range"
                min={0}
                max={1}
                step={0.01}
                value={knobValue(knob, generator)}
                onInput={(event) => {
                  const value = knob.from + Number(event.currentTarget.value) * (knob.to - knob.from)
                  setGenerator({
                    ...generator,
                    [knob.key]: Math.round(value * 1000) / 1000,
                  })
                }}
              />
              <output>{Math.round(knobValue(knob, generator) * 100)}</output>
            </label>
          ))}
          <details class="menu__more">
            <summary>{t('menu.fineTuning')}</summary>
            <Groups groups={GENERATOR_GROUPS} values={generator} onChange={setGenerator} />
          </details>
          <div class="menu__field">
            <span>{t('menu.dayLasts')}</span>
            <span class="menu__inline">
              {DAY_LENGTHS.map((seconds) => (
                <button type="button" key={seconds} class={seconds === weather.dayLength ? 'is-active' : undefined} onClick={() => setWeather({ ...weather, dayLength: seconds })}>
                  {seconds ? t('menu.minutes', { minutes: seconds / 60 }) : t('menu.dayStands')}
                </button>
              ))}
            </span>
          </div>
          <label class="menu__field menu__field--slider">
            <span>{t('menu.startHour')}</span>
            <input
              type="range"
              min={0}
              max={23.5}
              step={0.5}
              value={weather.startHour}
              onInput={(event) =>
                setWeather({
                  ...weather,
                  startHour: Number(event.currentTarget.value),
                })
              }
            />
            <output>{clock(weather.startHour)}</output>
          </label>
          <label class="menu__check">
            <input type="checkbox" checked={weather.changes} onChange={(event) => setWeather({ ...weather, changes: event.currentTarget.checked })} />
            {t('menu.weatherChanges')}
          </label>
        </div>
        <div class="menu__aside">
          <MapPreview config={generator} size={size} />
        </div>
      </form>
    </Window>
  )
}

/** Пределы стороны новой карты в тайлах. */
const WORLD_MIN = 64
const WORLD_MAX = 2048

/**
 * Новый мир с точными размерами: по генератору с настройками или чистая пустыня — заготовка, чтобы нарисовать карту в
 * редакторе. Мир сразу сохраняется в слот, без игроков: их стартовые наборы ставит игра или редактор.
 */
function NewWorld({ back, play, count }: { back(): void; play(launch: Launch): void; count: number }) {
  const [name, setName] = useState(() => t('menu.mapName', { number: count + 1 }))
  const [width, setWidth] = useState(256)
  const [height, setHeight] = useState(256)
  const [blank, setBlank] = useState(false)
  const [generator, setGenerator] = useState<GeneratorConfig>(() => ({ ...DEFAULT_CONFIG, seed: randomSeed() }))
  const [busy, setBusy] = useState(false)
  const side = (value: number) => Math.max(WORLD_MIN, Math.min(WORLD_MAX, Math.round(value) || WORLD_MIN))
  const create = async (edit: boolean) => {
    setBusy(true)
    const config: GeneratorConfig = { ...generator, ...(blank && { blank: true }) }
    const changes = generatorChanges(config)
    const slot = createSlot(name.trim() || t('menu.mapName', { number: count + 1 }), width, config.seed, undefined, Object.keys(changes).length ? changes : undefined, height)
    // В редактор — мир без игроков, сохранённый сразу; в игру — его заведёт воркер со стартовым набором.
    if (edit) await storeSave(slot.id, createSim({ generator: config, size: width, height }).save())
    play({ kind: edit ? 'editor' : 'save', slot: listSaves().find((other) => other.id === slot.id) ?? slot } as Launch)
  }
  const groups = blank ? [] : GENERATOR_GROUPS
  return (
    <Window
      title={t('menu.newMap')}
      back={back}
      wide
      actions={
        <>
          <button type="button" disabled={busy} onClick={() => create(false)}>
            {t('common.play')}
          </button>
          <button type="button" class="menu__primary" disabled={busy} onClick={() => create(true)}>
            {t('menu.toEditor')}
          </button>
        </>
      }
    >
      <div class="menu__setup">
        <div class="menu__form">
          <label class="menu__field">
            <span>{t('menu.name')}</span>
            <input value={name} maxLength={40} onInput={(event) => setName(event.currentTarget.value)} />
          </label>
          <div class="menu__field">
            <span>{t('menu.sizeTiles')}</span>
            <span class="menu__inline">
              <input type="number" min={WORLD_MIN} max={WORLD_MAX} step={16} value={width} title={t('menu.width')} onChange={(event) => setWidth(side(Number(event.currentTarget.value)))} />
              ×
              <input type="number" min={WORLD_MIN} max={WORLD_MAX} step={16} value={height} title={t('menu.height')} onChange={(event) => setHeight(side(Number(event.currentTarget.value)))} />
            </span>
          </div>
          <div class="menu__field">
            <span>{t('menu.terrain')}</span>
            <span class="menu__inline">
              <button type="button" class={!blank ? 'is-active' : undefined} onClick={() => setBlank(false)}>
                {t('menu.generator')}
              </button>
              <button type="button" class={blank ? 'is-active' : undefined} title={t('menu.blankTip')} onClick={() => setBlank(true)}>
                {t('menu.blank')}
              </button>
            </span>
          </div>
          {!blank && (
            <label class="menu__field">
              <span>{t('menu.seed')}</span>
              <span class="menu__inline">
                <input type="number" value={generator.seed} onInput={(event) => setGenerator({ ...generator, seed: Math.floor(Number(event.currentTarget.value)) || 0 })} />
                <button type="button" onClick={() => setGenerator({ ...generator, seed: randomSeed() })}>
                  {t('menu.random')}
                </button>
              </span>
            </label>
          )}
          {groups.length > 0 && <Groups groups={groups} values={generator} onChange={setGenerator} />}
        </div>
        <div class="menu__aside">
          <MapPreview config={{ ...generator, ...(blank && { blank: true }) }} size={width} height={height} />
        </div>
      </div>
    </Window>
  )
}

function Saves({ back, saves, refresh, play, create }: { back(): void; saves: SaveSlot[]; refresh(): void; play(launch: Launch): void; create(): void }) {
  const [error, setError] = useState('')
  const [confirming, setConfirming] = useState<string | null>(null)
  /** Открывает файл сохранения: edit — сразу в редакторе, не заводя слот (так правят мир сервера), иначе — в новый слот. */
  const pick = (edit: boolean) => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.kharos'
    input.onchange = async () => {
      const file = input.files?.[0]
      if (!file) return
      const bytes = new Uint8Array(await file.arrayBuffer())
      if (edit) return play({ kind: 'editor', file: bytes, name: file.name })
      try {
        await importSave(bytes)
        setError('')
      } catch (reason) {
        setError(reason instanceof Error ? reason.message : String(reason))
      }
      refresh()
    }
    input.click()
  }
  return (
    <Window
      title={t('menu.saves')}
      back={back}
      wide
      actions={
        <>
          <button onClick={() => pick(false)}>{t('menu.loadFile')}</button>
          <button title={t('menu.fileToEditorTip')} onClick={() => pick(true)}>
            {t('menu.fileToEditor')}
          </button>
          {/* Главное — «Играть» у сохранения; без сохранений главное — начать новый мир. */}
          <button class={saves.length ? undefined : 'menu__primary'} title={t('menu.newWorldTip')} onClick={create}>
            {t('menu.newWorld')}
          </button>
        </>
      }
    >
      {saves.length === 0 && <p class="menu__note">{t('menu.noSaves')}</p>}
      <ul class="menu__saves">
        {saves.map((slot) => (
          <li key={slot.id} class="menu__save">
            <div class="menu__save-info">
              <strong>{slot.name}</strong>
              <small>
                {date(slot.updated)} · {playtime(slot.tick)} · {t('menu.mapOf', { size: slot.height && slot.height !== slot.size ? `${slot.size}×${slot.height}` : slot.size })} ·{' '}
                {t('menu.seedOf', { seed: slot.seed })}
              </small>
            </div>
            <span class="menu__inline">
              <button class="menu__primary" onClick={() => play({ kind: 'save', slot })}>
                {t('common.play')}
              </button>
              <button title={t('menu.editorTip')} disabled={slot.updated === slot.created} onClick={() => play({ kind: 'editor', slot })}>
                {t('menu.editor')}
              </button>
              <button
                onClick={() => {
                  const name = prompt(t('menu.renamePrompt'), slot.name)?.trim()
                  if (!name) return
                  renameSave(slot.id, name)
                  refresh()
                }}
              >
                {t('menu.rename')}
              </button>
              <button
                title={t('menu.fileTip')}
                onClick={async () => {
                  const file = await exportSave(slot.id)
                  if (file) download(`${slot.name}.kharos`, file)
                  else setError(t('menu.notSavedYet'))
                }}
              >
                {t('menu.file')}
              </button>
              {confirming === slot.id ? (
                <button
                  class="menu__danger"
                  onClick={() => {
                    deleteSave(slot.id)
                    setConfirming(null)
                    refresh()
                  }}
                >
                  {t('menu.sure')}
                </button>
              ) : (
                <button onClick={() => setConfirming(slot.id)}>{t('menu.remove')}</button>
              )}
            </span>
          </li>
        ))}
      </ul>
      {error && <p class="menu__error">{error}</p>}
    </Window>
  )
}

function Network({ back, play }: { back(): void; play(launch: Launch): void }) {
  const [server, setServer] = useState(loadServer)
  const [recent, setRecent] = useState(recentServers)
  const join = (target: typeof server) => {
    try {
      localStorage.setItem(SERVER_KEY, JSON.stringify(target))
    } catch {
      // Адрес просто не запомнится.
    }
    play({ kind: 'server', ...target, name: cleanName(target.name) })
  }
  return (
    <Window
      title={t('menu.network')}
      back={back}
      actions={
        <button type="submit" form="network" class="menu__primary" disabled={!/^wss?:\/\/./.test(server.url)}>
          {t('common.connect')}
        </button>
      }
    >
      {recent.length > 0 && (
        <>
          <h3 class="menu__subtitle">{t('menu.recentServers')}</h3>
          <ul class="menu__saves">
            {recent.map((item) => (
              <li key={item.url} class="menu__save">
                <div class="menu__save-info">
                  <strong>{item.url}</strong>
                  <small>
                    {date(item.played)}
                    {item.name && ` · ${item.name}`}
                  </small>
                </div>
                <span class="menu__inline">
                  <button class="menu__primary" onClick={() => join({ ...server, url: item.url, name: server.name || item.name })}>
                    {t('common.play')}
                  </button>
                  <button
                    title={t('menu.forget')}
                    onClick={() => {
                      forgetServer(item.url)
                      setRecent(recentServers())
                    }}
                  >
                    ✕
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
      <form
        id="network"
        class="menu__form"
        onSubmit={(event) => {
          event.preventDefault()
          join(server)
        }}
      >
        <label class="menu__field">
          <span>{t('menu.nick')}</span>
          <input
            value={server.name}
            maxLength={NAME_LENGTH}
            placeholder={t('menu.nickHint')}
            onInput={(event) => setServer({ ...server, name: event.currentTarget.value })}
          />
        </label>
        <label class="menu__field">
          <span>{t('menu.serverAddress')}</span>
          {/* «Этот компьютер» подставляет адрес — и стоит рядом с ним, а не среди действий окна. */}
          <span class="menu__inline">
            <input
              value={server.url}
              placeholder="ws://host:port"
              onInput={(event) => {
                const url = event.currentTarget.value.trim()
                setServer({ ...server, url })
              }}
            />
            <button type="button" onClick={() => setServer({ ...server, url: localServerUrl() })}>
              {t('menu.thisComputer')}
            </button>
          </span>
        </label>
        <label class="menu__field">
          <span>{t('menu.lag')}</span>
          <input
            type="number"
            min={0}
            value={server.lag}
            title={t('menu.lagTip')}
            onInput={(event) => setServer({ ...server, lag: Math.max(0, Number(event.currentTarget.value) || 0) })}
          />
        </label>
        {isInsecure(server.url) && <p class="menu__note is-warn">{t('errors.insecure')}</p>}
        <p class="menu__note">{t('menu.sharedWorld')}</p>
      </form>
    </Window>
  )
}

interface SettingsProps {
  back(): void
  /** Громкость или выключение звука поменялись: музыка меню подстраивается сразу. */
  onSound(): void
}

/** Настройки самого игрока. Всё, что относится к миру (погода, размер, зерно), задаётся при создании игры. */
export function Settings({ back, onSound }: SettingsProps) {
  const [muted, setMuted] = useState(loadMuted)
  const [volume, setVolume] = useState(loadVolume)
  const [levels, setLevels] = useState(() => Object.fromEntries(CHANNELS.map(({ channel }) => [channel, loadLevel(channel)])) as Record<Channel, number>)
  return (
    <Window title={t('common.settings')} back={back}>
      <div class="menu__form">
        <h3 class="menu__group">{t('menu.language')}</h3>
        <span class="menu__inline">
          {LANGS.map(({ lang, name }) => (
            <button type="button" key={lang} class={lang === currentLang() ? 'is-active' : undefined} onClick={() => void chooseLang(lang)}>
              {name}
            </button>
          ))}
        </span>
        <h3 class="menu__group">{t('menu.sound')}</h3>
        <label class="menu__check">
          <input
            type="checkbox"
            checked={!muted}
            onChange={(event) => {
              storeMuted(!event.currentTarget.checked)
              setMuted(!event.currentTarget.checked)
              onSound()
            }}
          />
          {t('menu.soundAndMusic')}
        </label>
        <label class="menu__field menu__field--slider">
          <span>{t('menu.master')}</span>
          <input
            type="range"
            min={0}
            max={1}
            step={0.05}
            value={volume}
            disabled={muted}
            onInput={(event) => {
              storeVolume(Number(event.currentTarget.value))
              setVolume(Number(event.currentTarget.value))
              onSound()
            }}
          />
          <output>{Math.round(volume * 100)}%</output>
        </label>
        {/* Громкость групп — доля общей. */}
        {CHANNELS.map(({ channel }) => (
          <label key={channel} class="menu__field menu__field--slider">
            <span>{t(`menu.channels.${channel}`)}</span>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={levels[channel]}
              disabled={muted}
              onInput={(event) => {
                const value = Number(event.currentTarget.value)
                storeLevel(channel, value)
                setLevels({ ...levels, [channel]: value })
                onSound()
              }}
            />
            <output>{Math.round(levels[channel] * 100)}%</output>
          </label>
        ))}
      </div>
    </Window>
  )
}

/** Об игре: что это, кто делает, где код. */
function About({ back }: { back(): void }) {
  return (
    <Window title={t('menu.about')} back={back}>
      <div class="menu__form">
        <p class="menu__about">
          {t('menu.aboutText')}
        </p>
        <h3 class="menu__group">{t('menu.authors')}</h3>
        <ul class="menu__authors">
          {AUTHORS.map((author) => (
            <li key={author.name}>
              <a class="menu__link" href={author.url} target="_blank" rel="noopener noreferrer">
                {author.name}
              </a>
            </li>
          ))}
        </ul>
        <h3 class="menu__group">{t('menu.sourceCode')}</h3>
        <a class="menu__link" href={REPOSITORY} target="_blank" rel="noopener noreferrer">
          github.com/vicimpa/kharos
        </a>
        <p class="menu__note">{t('menu.protocolVersion', { version: PROTOCOL_VERSION })}</p>
      </div>
    </Window>
  )
}
