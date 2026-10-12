import { complete, quoteName } from '../net/chatCommands'
import type { ComponentChildren } from 'preact'
import type { HudState, Stack } from '../game/hud'
import { MINIMAP_SIZE, type Minimap } from '../game/minimap'
import type { PaveTool } from '../game/scene'
import { buildingPortrait, pavePortrait, unitPortrait } from '../game/portraits'
import { goodIcon } from '../game/goodIcons'
import { useEffect, useRef, useState } from 'preact/hooks'
import { REWARDS, resourceOf, type UnitType, type BuildingType, type Command, type Good, type Reward } from '../sim'
import { GRID_KEYS, commandsOf, dealOf, keyLabel, type Deal, type FerryDraft, type Page, type Slot } from './commands'
import { rich, t, type Key } from '../i18n'

interface HudProps {
  state: HudState
  send: (command: Command) => void
  /** Начать выбор места под здание; null — отменить. */
  place: (building: BuildingType | null) => void
  /** Начать укладку или снятие покрытия; null — отменить. */
  pave: (tool: PaveTool | null) => void
  /** Начать набор маршрута грузовикам; false — отменить. */
  route: (start: boolean) => void
  /** Начать выбор зданий, которые обслуживают грузовики; false — отменить. */
  serve: (start: boolean) => void
  /** Начать набор точек патруля бойцам; false — отменить. */
  patrol: (start: boolean) => void
  minimap: Minimap
  /** Камера в точку карты, в тайлах: сразу. */
  lookAt: (x: number, y: number) => void
  /** Камера летит к точке карты, в тайлах. */
  flyTo: (x: number, y: number) => void
  /** Камера на выделенное. */
  lookAtSelection: () => void
  /** Оставить в выделении только этот вид юнитов; remove — убрать его. */
  narrow: (type: UnitType, remove: boolean) => void
  /** Выделенных — в точку карты. */
  moveSelected: (x: number, y: number) => void
  /** Написать в чат сетевой игры. */
  say: (text: string) => void
  /** Кнопки меню справа на верхней полосе. */
  menu: ComponentChildren
}

const percent = (value: number) => `${Math.round(value * 100)}%`

/** Стороны квадратов под картинки, в пикселях экрана: портреты рисуются ровно под них, см. portraits.ts. */
const PORTRAIT = 96
const TILE = 32
const QUEUED = 24

/** Строка списка игроков, см. HudState. */
type PlayerRow = HudState['players'][number]

/**
 * Кнопки союза в списке игроков: что можно сделать с этим игроком прямо сейчас, см. allies.ts. Союзом распоряжается
 * глава: он исключает рядовых, а рядовой может только выйти сам — это кнопка в своей строке. Приглашать и отвечать
 * согласием тоже может лишь глава или одиночка, поэтому у рядового таких кнопок нет; чужого рядового он не видит и
 * получит отказ хоста. Команды те же, что набираются в чате: кнопка их просто посылает.
 */
function allyActions(row: PlayerRow, self: PlayerRow | undefined): { label: Key; command: string }[] {
  const quoted = quoteName(row.name)
  const leads = !!self && (self.head < 0 || self.head === self.player)
  if (row.own) {
    if (row.head < 0) return []
    return [{ label: row.head === row.player ? 'hud.allyDissolve' : 'hud.allyExit', command: '/unally' }]
  }
  if (row.relation === 'ally') return leads && row.head === self!.player ? [{ label: 'hud.allyKick', command: `/unally ${quoted}` }] : []
  if (row.relation === 'allyAsked') {
    const decline: { label: Key; command: string } = { label: 'hud.allyDecline', command: `/unally ${quoted}` }
    return leads ? [{ label: 'hud.allyAccept', command: `/ally ${quoted}` }, decline] : [decline]
  }
  if (row.relation === 'allyOffered') return [{ label: 'hud.allyWithdraw', command: `/unally ${quoted}` }]
  return leads ? [{ label: 'hud.allyOffer', command: `/ally ${quoted}` }] : []
}

/** Значок груза цвета груза и количество; название — во всплывающей подсказке. */
function Res({ resource, amount, of }: { resource: Good; amount?: number; of?: number }) {
  return (
    <span class="hud__res" data-tip={t(`goods.${resource}`)}>
      <img src={goodIcon(resource)} alt="" />
      {amount !== undefined && (of !== undefined ? `${amount}/${of}` : amount)}
    </span>
  )
}

/** Ресурсы в строку. */
const Stacks = ({ items }: { items: Stack[] }) => (
  <>
    {items.map(({ resource, amount }) => (
      <Res key={resource} resource={resource} amount={amount} />
    ))}
  </>
)

/** Полоса готовности с подписью. */
const Progress = ({ value, children }: { value: number; children: ComponentChildren }) => (
  <div class="hud__progress">
    <span style={{ width: percent(value) }} />
    <em>{children}</em>
  </div>
)

/**
 * Подсказка для всего, у чего есть data-tip: появляется сразу у курсора, а не через секунду, как системная,
 * и не обрезается краями ячеек и прокручиваемых блоков.
 */
function Tip() {
  const [tip, setTip] = useState<{ text: string; card: string | null; x: number; y: number } | null>(null)
  useEffect(() => {
    const onMove = (event: PointerEvent) => {
      const owner = event.target instanceof Element ? event.target.closest('[data-tip]') : null
      const text = owner?.getAttribute('data-tip')
      setTip(text ? { text, card: owner!.getAttribute('data-card'), x: event.clientX, y: event.clientY } : null)
    }
    // Щелчок гасит подсказку: кнопка могла открыть окно поверх, а подсказка осталась бы висеть под ним.
    const onDown = () => setTip(null)
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerdown', onDown)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerdown', onDown)
    }
  }, [])
  if (!tip) return null
  // Карточка берётся из CARDS при каждой отрисовке: цена и доступность в ней свежие, пока курсор стоит на месте.
  const card = tip.card !== null ? CARDS.get(tip.card) : undefined
  if (card) {
    // Карточка — над сеткой команд, правым краем у курсора: сетка у правого края экрана. Над всей сеткой, а не
    // над курсором — иначе она закрывает соседние ячейки, о которых и рассказывает.
    const grid = document.querySelector('.bar__grid')?.getBoundingClientRect()
    const above = grid ? window.innerHeight - grid.top + 12 : window.innerHeight - tip.y + 24
    return (
      <div class="hud tip tip--card" style={{ right: `${Math.max(8, window.innerWidth - tip.x - 40)}px`, bottom: `${above}px` }}>
        {card}
      </div>
    )
  }
  // Над курсором, а у верхнего края — под ним; у правого края — левее курсора.
  const below = tip.y < 60
  const left = tip.x > window.innerWidth - 260
  return (
    <div
      class="hud tip"
      style={{
        left: left ? undefined : `${tip.x + 14}px`,
        right: left ? `${window.innerWidth - tip.x + 14}px` : undefined,
        top: `${below ? tip.y + 20 : tip.y - 36}px`,
      }}
    >
      {tip.text}
    </div>
  )
}

/** Сколько секунд сообщение о награде висит на экране. */
const TOAST_SECONDS = 5

/**
 * Награды, полученные только что: каждая показывается TOAST_SECONDS. Те, что были до входа в игру, не показываются:
 * пока мир не пришёл от хоста (loaded), всё, что в нём есть, считается уже виденным.
 */
function useNewRewards(rewards: string[], loaded: boolean) {
  const seen = useRef(rewards.length)
  /** Был ли мир уже загружен при прошлой проверке: в обновлении, где он пришёл, все его награды — старые. */
  const ready = useRef(false)
  const [fresh, setFresh] = useState<string[]>([])
  useEffect(() => {
    const added = ready.current ? rewards.slice(seen.current) : []
    ready.current = loaded
    // Список стал короче — мир начался заново: считаем с нуля.
    seen.current = rewards.length
    if (!added.length) return
    setFresh((list) => [...list, ...added])
    // Таймер не отменяется при следующей награде: иначе прежнее сообщение осталось бы висеть.
    setTimeout(() => setFresh((list) => list.filter((key) => !added.includes(key))), TOAST_SECONDS * 1000)
  }, [rewards.length, loaded])
  return fresh
}

/**
 * Мини-карта: перерисовывается каждый кадр. Левая кнопка (и протяжка) ставит туда камеру, правая — посылает
 * туда выделенных.
 */
/** Сколько секунд сообщение чата видно, пока чат закрыт, и сколько из них оно гаснет. */
const CHAT_SHOWN = 10
const CHAT_FADE = 1
/** Сколько последних сообщений видно в закрытом чате. */
const CHAT_RECENT = 8

/**
 * Чат, как в Minecraft: Enter открывает строку ввода, Enter отправляет, Escape закрывает. Закрытый чат показывает
 * свежие сообщения, и через CHAT_SHOWN секунд они гаснут; открытый — всю историю.
 */
/** Сколько подсказок дополнения видно над строкой чата. */
const CHAT_SUGGESTIONS = 6

interface ChatProps {
  lines: NonNullable<HudState['chat']>
  picking: boolean
  commands: HudState['commands']
  admin: boolean
  names: string[]
  say: (text: string) => void
}

function Chat({ lines, picking, commands, admin, names, say }: ChatProps) {
  const [open, setOpen] = useState(false)
  const [draft, setDraft] = useState('')
  /** Строка чата спрашивает пароль администратора: набранное скрыто и уходит как /admin <пароль>. */
  const [password, setPassword] = useState(false)
  /** Какая подсказка дополнения выбрана стрелками. */
  const [chosen, setChosen] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  const pickingRef = useRef(picking)
  pickingRef.current = picking
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLInputElement || event.repeat || pickingRef.current) return
      // «/» открывает чат сразу с началом команды; на русской раскладке та же клавиша — точка.
      const slash = event.key === '/' || event.code === 'Slash' || event.code === 'NumpadDivide'
      if (event.code !== 'Enter' && event.code !== 'NumpadEnter' && !slash) return
      event.preventDefault()
      if (slash) setDraft('/')
      setOpen(true)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
  useEffect(() => {
    if (open) input.current?.focus()
  }, [open])
  const close = () => {
    setOpen(false)
    setDraft('')
    setPassword(false)
    setChosen(0)
    input.current?.blur()
  }
  const suggestions = password ? [] : complete(draft, commands, names).slice(0, CHAT_SUGGESTIONS)
  const pick = Math.min(chosen, suggestions.length - 1)
  const edit = (text: string) => {
    setDraft(text)
    setChosen(0)
  }
  const now = performance.now()
  const shown = open ? lines : lines.slice(-CHAT_RECENT).filter((line) => now - line.at < CHAT_SHOWN * 1000)
  if (!open && !shown.length) return null
  return (
    <div class={`hud chat${open ? ' is-open' : ''}`}>
      <div class="chat__lines">
        {shown.map((line, i) => {
          const left = CHAT_SHOWN - (now - line.at) / 1000
          return (
            <div key={i} class={`chat__line${line.system ? ' is-system' : ''}`} style={open ? undefined : { opacity: Math.min(1, left / CHAT_FADE) }}>
              {line.system ? (
                line.name ? `${line.name} ${line.text}` : line.text
              ) : line.whisper ? (
                <i class="chat__whisper">
                  &lt;{line.name} → {line.whisper}&gt; {line.text}
                </i>
              ) : (
                <>
                  <b class={line.own ? 'is-own' : undefined}>&lt;{line.name}&gt;</b> {line.text}
                </>
              )}
            </div>
          )
        })}
      </div>
      {open && suggestions.length > 0 && (
        <div class="chat__suggestions">
          {suggestions.map((item, i) => (
            <div
              key={item.text + item.label}
              class={`chat__suggestion${i === pick ? ' is-chosen' : ''}`}
              // До blur строки: иначе чат закроется раньше, чем подсказка подставится.
              onMouseDown={(event) => {
                event.preventDefault()
                edit(item.text)
              }}
            >
              <b>{item.label}</b> <span>{item.help}</span>
            </div>
          ))}
        </div>
      )}
      {open && (
        <input
          ref={input}
          class={`chat__input${admin ? ' is-admin' : ''}`}
          // Типы Preact не дают выбрать type выражением: "text" и "password" у них в разных перегрузках.
          type={(password ? 'password' : 'text') as 'password'}
          placeholder={password ? t('hud.adminPassword') : admin ? t('hud.adminMode') : undefined}
          value={draft}
          maxLength={200}
          onInput={(event) => edit(event.currentTarget.value)}
          onBlur={close}
          onKeyDown={(event) => {
            // Клавиши чата не доходят ни до игры, ни до сетки команд.
            event.stopPropagation()
            if (event.code === 'Escape') close()
            if (event.code === 'Tab') {
              event.preventDefault()
              if (suggestions[pick]) edit(suggestions[pick].text)
            }
            if ((event.code === 'ArrowUp' || event.code === 'ArrowDown') && suggestions.length) {
              event.preventDefault()
              const step = event.code === 'ArrowUp' ? -1 : 1
              setChosen((pick + step + suggestions.length) % suggestions.length)
            }
            if (event.code === 'Enter' || event.code === 'NumpadEnter') {
              // Голый /admin спрашивает пароль отдельно, скрытым полем: в строке чата его видно.
              // Команда с паролем — /admin — спрашивает его скрытым полем; набранное после неё в строке не уходит.
              const secret = commands.find((command) => command.secret && draft.trim().toLowerCase().split(' ')[0] === `/${command.name}`)
              if (!password && secret) {
                setPassword(true)
                setDraft('')
                return
              }
              if (password) {
                if (draft) say(`/admin ${draft}`)
              } else if (draft.trim()) say(draft)
              close()
            }
          }}
        />
      )}
    </div>
  )
}

function MinimapView({ minimap, lookAt, moveSelected }: Pick<HudProps, 'minimap' | 'lookAt' | 'moveSelected'>) {
  const ref = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const context = ref.current!.getContext('2d')!
    let frame = requestAnimationFrame(function draw() {
      minimap.draw(context)
      frame = requestAnimationFrame(draw)
    })
    return () => cancelAnimationFrame(frame)
  }, [minimap])

  const tileOf = (event: PointerEvent) => {
    const box = ref.current!.getBoundingClientRect()
    const u = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width))
    const v = Math.min(1, Math.max(0, (event.clientY - box.top) / box.height))
    return minimap.tileAt(u, v)
  }
  return (
    <canvas
      ref={ref}
      class="bar__minimap"
      width={MINIMAP_SIZE}
      height={MINIMAP_SIZE}
      onPointerDown={(event) => {
        const { x, y } = tileOf(event)
        if (event.button === 2) return moveSelected(x, y)
        if (event.button !== 0) return
        ref.current!.setPointerCapture(event.pointerId)
        lookAt(x, y)
      }}
      onPointerMove={(event) => {
        if (!(event.buttons & 1)) return
        const { x, y } = tileOf(event)
        lookAt(x, y)
      }}
      onContextMenu={(event) => event.preventDefault()}
    />
  )
}

/** Картинка ячейки: портрет здания или юнита, значок груза или покрытия, или ничего. */
function SlotImage({ slot }: { slot: Slot }) {
  if (slot.building) return <img src={buildingPortrait(slot.building, TILE)} alt="" />
  if (slot.unit) return <img src={unitPortrait(slot.unit, TILE)} alt="" />
  if (slot.good) return <img class="bar__good" src={goodIcon(slot.good)} alt="" />
  if (slot.pave) return <img src={pavePortrait(slot.pave, TILE)} alt="" />
  return null
}

/** Карточки ячеек сетки для подсказки: ячейка кладёт сюда свою при каждой отрисовке, подсказка находит её по data-card. */
const CARDS = new Map<string, ComponentChildren>()

/** Карточка ячейки: название с клавишей, пояснение, полная цена и энергия. */
function SlotCard({ slot, hotkey }: { slot: Slot; hotkey: string }) {
  const price = slot.cost !== undefined || slot.materials?.length || slot.power
  return (
    <div class="card">
      <div class="card__title">
        {slot.label} <kbd>{hotkey}</kbd>
      </div>
      {slot.about && <div class="card__text">{slot.about}</div>}
      {slot.title && <div class={slot.about && slot.disabled ? 'card__text is-warn' : 'card__text'}>{slot.title}</div>}
      {price ? (
        <div class="card__price">
          {slot.cost !== undefined && (
            <span class={slot.gain ? 'card__credits is-gain' : 'card__credits'}>
              <span class="hud__coin" />
              {slot.gain ? `+${slot.cost}` : slot.cost}
            </span>
          )}
          {slot.materials?.length ? <Stacks items={slot.materials} /> : null}
          {slot.power ? <span class="hud__power">⚡{slot.power > 0 ? `+${slot.power}` : slot.power}</span> : null}
        </div>
      ) : null}
    </div>
  )
}

/** Есть ли у ячейки картинка: без неё название и цена занимают всю ширину. */
const pictured = (slot: Slot) => !!(slot.building || slot.unit || slot.good || slot.pave)

/** Сетка команд 4×3: ячейка — кнопка со своей клавишей. */
function CommandGrid({ slots }: { slots: (Slot | null)[] }) {
  return (
    <div class="bar__grid">
      {slots.map((slot, i) => {
        const key = keyLabel(GRID_KEYS[i])
        if (!slot) return <div key={i} class="bar__cell bar__cell--empty" />
        CARDS.set(String(i), <SlotCard slot={slot} hotkey={key} />)
        return (
          <button
            key={i}
            class={['bar__cell', slot.group !== undefined && 'bar__cell--group', slot.active && 'is-active', !pictured(slot) && 'is-bare']
              .filter(Boolean)
              .join(' ')}
            disabled={slot.disabled}
            data-tip={slot.label}
            data-card={String(i)}
            onClick={slot.run}
          >
            <SlotImage slot={slot} />
            {slot.lamp !== undefined && <span class={slot.lamp ? 'bar__lamp is-on' : 'bar__lamp'} />}
            <span class="bar__label">{slot.label}</span>
            <kbd>{key}</kbd>
            {/* В ячейке — только кредиты: полная цена с ресурсами и энергией — в карточке при наведении. У раздела —
                сколько в нём кнопок. */}
            <span class="bar__price">
              {slot.group !== undefined && `${slot.group} ▸`}
              {slot.cost !== undefined && (
                <b class={slot.gain ? 'is-gain' : undefined}>
                  <span class="hud__coin" />
                  {slot.gain ? `+${slot.cost}` : slot.cost}
                </b>
              )}
              {slot.cost === undefined && slot.materials?.length ? <small>…</small> : null}
            </span>
          </button>
        )
      })}
    </div>
  )
}

/** Что уже набрано в подарок союзнику: груз по видам и деньги — как это видно в панели сведений. */
function listOfDraft(ferry: FerryDraft) {
  const parts = (Object.entries(ferry.goods) as [Good, number][]).map(([resource, amount]) => `${t(`goods.${resource}`)} ×${Math.floor(amount)}`)
  if (ferry.credits > 0) parts.push(`${ferry.credits} ${t('hud.cr')}`)
  return parts.join(', ')
}

/** Сведения о выбранном: портрет, название, прочность и всё, что здание или отряд делает сейчас. */
function Info({ state, lookAtSelection, narrow, deal, ferry, setAmount, confirm }: {
  state: HudState
  lookAtSelection: () => void
  narrow: HudProps['narrow']
  /** Набираемая сделка космопорта: количество можно вписать с клавиатуры, Enter — отправить. */
  deal: (NonNullable<ReturnType<typeof dealOf>> & { buying: boolean }) | null
  /** Набираемый подарок союзнику: кому и что уже положено в корабль. */
  ferry: FerryDraft | null
  setAmount: (amount: number) => void
  confirm: () => void
}) {
  const { units, building, deposit, site, construction, conversion, production, assembly, trade } = state
  const nameOf = (player: number) => state.players.find((one) => one.player === player)?.name ?? ''
  const many = units.length > 1 || (units[0]?.count ?? 0) > 1
  const health = building !== null ? (state.health ?? 1) : (state.army?.health ?? 1)

  const title =
    building !== null
      ? state.buildings > 1
        ? state.mixed ? t('hud.buildingsCount', { count: state.buildings }) : `${t(`buildings.${building}`)} ×${state.buildings}`
        : site ? t(site.demolish ? 'hud.demolition' : 'hud.construction', { name: t(`buildings.${building}`) }) : t(`buildings.${building}`)
      : units.map(({ type, count }) => (count > 1 ? `${t(`units.${type}`)} ×${count}` : t(`units.${type}`))).join(', ')

  return (
    <div class="bar__info">
      {many ? (
        <div class="bar__units">
          {units.map(({ type, count }) => (
            <button
              key={type}
              class="bar__unit"
              data-tip={t('hud.narrowTip', { name: t(`units.${type}`) })}
              onClick={(event) => narrow(type, event.shiftKey)}
            >
              <img src={unitPortrait(type, TILE)} alt="" />
              {count > 1 && <b>{count}</b>}
            </button>
          ))}
        </div>
      ) : (
        <button class="bar__portrait" data-tip={t('hud.showOnMap')} onClick={lookAtSelection}>
          <img src={building !== null ? buildingPortrait(building, PORTRAIT) : unitPortrait(units[0].type, PORTRAIT)} alt="" />
          <span class="bar__health" style={{ width: percent(health) }} data-low={health < 0.35 || undefined} />
        </button>
      )}

      <div class="bar__details">
        <header class="hud__title">{title}</header>

        {state.power && (
          <div class={state.power.demand > state.power.produced ? 'hud__hint hud__power is-short' : 'hud__hint hud__power'}>
            {t('hud.zonePower', { demand: state.power.demand, produced: state.power.produced })}
            {state.power.demand > state.power.produced && t('hud.overloaded')}
          </div>
        )}
        {state.coreless && <div class="hud__hint is-short">{t('hud.coreless')}</div>}
        {state.unlinked && <div class="hud__hint is-short">{t('hud.unlinked')}</div>}
        {state.starved && <div class="hud__hint hud__power is-short">{t('hud.starved')}</div>}
        {state.switchable && !state.switchable.on && <div class="hud__hint is-short">{t('hud.switchedOff')}</div>}
        {state.health !== null && (
          <div class="hud__hint">{t('hud.health', { percent: percent(state.health), repair: state.repair })}</div>
        )}
        {state.army && state.army.health < 1 && <div class="hud__hint">{t('hud.armyHealth', { percent: percent(state.army.health) })}</div>}
        {state.army && state.army.armed > 0 && !construction && (
          <div class="hud__hint">{t('hud.armed')}</div>
        )}

        {state.stored?.slots && (
          <div class="hud__hint">
            {t('hud.stock')}:{' '}
            {state.stored.slots.map(({ resource, amount, of }) => (
              <Res key={resource} resource={resource} amount={amount} of={of} />
            ))}
          </div>
        )}
        {state.stored && !state.stored.slots && (
          <div class="hud__hint">
            {state.stored.buffer ? t('hud.orderMaterials') : state.stored.store ? t('hud.storage') : t('hud.stock')}: <Stacks items={state.stored.items} />
            {state.stored.items.length === 0 && t('common.empty')}
            {!state.stored.buffer && t('hud.holds', { capacity: state.stored.capacity })}
            {state.reserve && state.reserve.amount > 0 && t('hud.reserve', { amount: state.reserve.amount })}
          </div>
        )}
        {state.refinery && (
          <div class="hud__hint">
            {rich<ComponentChildren>('hud.refines', {
              ore: <Res resource={state.refinery.ore} />,
              oreName: t(`goods.${state.refinery.ore}`),
              product: <Res resource={resourceOf(state.refinery.ore)} />,
              productName: t(`goods.${resourceOf(state.refinery.ore)}`),
              intake: state.refinery.intake,
            })}
          </div>
        )}
        {state.harvest && (
          <div class="hud__hint">
            {state.harvest.kind
              ? rich<ComponentChildren>('hud.digs', { icon: <Res resource={state.harvest.kind} />, name: t(`goods.${state.harvest.kind}`) })
              : state.harvest.parked
                ? t('hud.parked')
                : state.harvest.seek && state.harvest.seek !== 'any'
                  ? rich<ComponentChildren>('hud.seeks', { icon: <Res resource={state.harvest.seek} />, name: t(`goods.${state.harvest.seek}`) })
                  : t('hud.seeksAny')}
            {t('hud.harvestTip')}
          </div>
        )}
        {state.ammo && (
          <div class={state.ammo.have <= 0 ? 'hud__hint is-short' : 'hud__hint'}>
            {t('hud.ammo')}: <Res resource="ammo" amount={state.ammo.have} of={state.ammo.capacity} />
            {state.ammo.have <= 0 ? t('hud.ammoEmpty') : t('hud.ammoTip')}
          </div>
        )}
        {assembly && (
          <>
            <div class="hud__hint">
              {rich<ComponentChildren>('hud.recipe', {
                inputs: <Stacks items={assembly.inputs} />,
                output: <Res resource={assembly.recipe} amount={assembly.yield} />,
                seconds: assembly.seconds,
              })}
            </div>
            <Progress value={assembly.progress}>
              {t('hud.assembly', {
                name: t(`goods.${assembly.recipe}`),
                have: assembly.have,
                state: t(
                  `hud.assemblyStates.${!assembly.on ? (assembly.progress > 0 ? 'offFinishing' : 'off') : assembly.progress > 0 ? 'working' : assembly.full ? 'full' : 'waiting'}`,
                ),
              })}
            </Progress>
            {!assembly.on && <div class="hud__hint is-short">{t('hud.plantOff')}</div>}
          </>
        )}
        {state.materials && (
          <div class={state.materials.waiting ? 'hud__hint is-short' : 'hud__hint'}>
            {t('hud.materials')}:{' '}
            {state.materials.items.map(({ resource, have, need }) => (
              <Res key={resource} resource={resource} amount={have} of={need} />
            ))}
            {state.materials.waiting && t('hud.materialsWaiting')}
          </div>
        )}
        {state.cargo && (
          <div class="hud__hint">
            {rich<ComponentChildren>('hud.cargo', {
              items: state.cargo.items.length ? <Stacks items={state.cargo.items} /> : t('common.empty'),
              capacity: state.cargo.capacity,
            })}
            {state.haul?.routing !== null && state.haul?.routing !== undefined
              ? t('hud.routing', { stops: state.haul.routing })
              : state.haul?.serving !== null && state.haul?.serving !== undefined
              ? t('hud.serving', { count: state.haul.serving })
              : state.cargo.bound > 0
              ? t('hud.haulsOre')
              : state.haul && state.haul.served > 0
                ? t('hud.serves')
              : state.haul && state.haul.routed > 0
                ? t('hud.onRoute')
              : state.cargo.busy > 0
                ? t('hud.onRequest')
                : t('hud.free')}
          </div>
        )}
        {trade &&
          (trade.order ? (
            <Progress value={trade.order.flight ?? trade.order.delivered / trade.order.wanted}>
              {trade.order.buy
                ? t('hud.buying', { name: t(`goods.${trade.order.resource}`), wanted: trade.order.wanted, paid: trade.order.wanted * trade.order.price })
                : trade.order.flight !== null
                ? t('hud.shipping', { name: t(`goods.${trade.order.resource}`), wanted: trade.order.wanted, total: trade.order.wanted * trade.order.price })
                : t('hud.hauling', { name: t(`goods.${trade.order.resource}`), delivered: trade.order.delivered, wanted: trade.order.wanted })}
            </Progress>
          ) : trade.transfer ? (
            <Progress value={trade.transfer.flight}>
              {t('hud.ferrying', { name: nameOf(trade.transfer.to), percent: percent(trade.transfer.flight) })}
            </Progress>
          ) : deal ? (
            <div class="hud__hint">
              {t(deal.buying ? 'grid.buy' : 'grid.sell')}: <Res resource={deal.resource} amount={deal.amount} />{' '}
              <input
                class="hud__amount"
                type="number"
                min={0}
                max={deal.max}
                value={deal.amount}
                onInput={(event) => setAmount(Math.max(0, Math.min(deal.max, Math.floor(Number(event.currentTarget.value)) || 0)))}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') confirm()
                  if (event.key === 'Enter' || event.key === 'Escape') event.currentTarget.blur()
                }}
              />
              {deal.buying
                ? t('hud.dealBuy', { total: deal.amount * deal.price, room: deal.room - deal.amount })
                : t('hud.dealSell', { total: deal.amount * deal.price, left: deal.max - deal.amount })}
            </div>
          ) : (
            <div class="hud__hint">
              {trade.offers.length ? t('hud.sellHint') : t('hud.nothingToSell')}
              {t('hud.buyHint')}
            </div>
          ))}
        {ferry && (
          <div class="hud__hint">
            {t('hud.ferryDraft', { name: ferry.name, list: listOfDraft(ferry) || t('common.empty') })}
          </div>
        )}
        {deposit && (
          <div class="hud__hint">
            {rich<ComponentChildren>('hud.depositLeft', {
              icon: <Res resource={deposit.kind} />,
              name: t(`goods.${deposit.kind}`),
              left: deposit.left > 0 ? t('hud.left', { amount: deposit.left }) : t('hud.depositDepleted'),
            })}
          </div>
        )}
        {state.level && (
          <div class="hud__hint">
            {state.level.level > 1
              ? t('hud.level', { level: state.level.level, max: state.level.max, gain: t(`levelGains.${state.level.gain}`), factor: state.level.factor })
              : t('hud.levelBase', { level: state.level.level, max: state.level.max })}
          </div>
        )}
        {state.level?.raising && (
          <Progress value={state.level.raising.progress}>
            {state.level.raising.progress > 0 ? t('hud.upgrading', { percent: percent(state.level.raising.progress) }) : t('hud.waitsToUpgrade')}
          </Progress>
        )}
        {state.research && !state.research.current && <div class="hud__hint">{t('hud.researchIdle')}</div>}
        {state.research?.current && (
          <Progress value={state.research.current.progress}>
            {t('hud.researching', { name: t(`research.${state.research.current.topic}`), percent: percent(state.research.current.progress) })}
          </Progress>
        )}
        {site && (
          <Progress value={site.progress}>
            {site.demolish
              ? site.progress < 1
                ? t('hud.demolishing', { percent: percent(site.progress) })
                : t('hud.waitsToDemolish')
              : site.started
                ? t('hud.building', { percent: percent(site.progress) })
                : site.blocked
                  ? t('hud.blocked')
                  : t('hud.waitsBuilder')}
          </Progress>
        )}
        {conversion && conversion.progress !== null && (
          <Progress value={conversion.progress}>
            {conversion.blocked ? t('hud.blocked') : conversion.kind === 'deploy' ? t('hud.deploying') : t('hud.packing')}
          </Progress>
        )}
        {production && production.queue.length > 0 && (
          <>
            <Progress value={production.progress}>{t(`units.${production.queue[0]}`)}</Progress>
            <div class="bar__queue">
              {production.queue.map((unit, i) => (
                <img key={i} src={unitPortrait(unit, QUEUED)} alt={t(`units.${unit}`)} data-tip={t(`units.${unit}`)} />
              ))}
            </div>
          </>
        )}
        {production?.building && (
          <div class="hud__hint">
            {production.rally ? t('hud.rallySet') : t('hud.rallyTip')}
          </div>
        )}
        {state.tactics?.picking && (
          <div class="hud__hint">{t('hud.patrolPick')}</div>
        )}
        {construction?.paving && (
          <div class="hud__hint">
            {construction.paving.tool === 'remove' ? t('hud.pavingRemove') : t('hud.paving', { tiles: construction.paving.tiles, cost: construction.paving.cost })}
            {construction.paving.tool !== 'remove' && construction.paving.short ? t('hud.pavingShort', { short: construction.paving.short }) : null}
            {t('hud.pavingEnd')}
          </div>
        )}
        {construction && !construction.placing && !construction.paving && (
          <div class="hud__hint">{t('hud.builderTip')}</div>
        )}
        {construction?.placing && <div class="hud__hint">{t('hud.placingTip')}</div>}
      </div>
    </div>
  )
}

/** Интерфейс игрока: верхняя полоса со счётом и нижняя панель — мини-карта, выбранное, сетка команд. */
export function Hud({ state, send, place, pave, route, serve, patrol, minimap, lookAt, flyTo, lookAtSelection, narrow, moveSelected, say, menu }: HudProps) {
  const selected = state.units.length > 0 || state.building !== null
  const fresh = useNewRewards(state.rewards, state.loaded)

  // Страница сетки строителя сбрасывается, когда выбрано другое.
  const [page, setPage] = useState<Page>('root')
  const selectionKey = `${state.building}:${state.units.map(({ type, count }) => `${type}${count}`).join()}`
  useEffect(() => setPage(state.construction?.placing || state.construction?.paving ? page : 'root'), [selectionKey])
  // Сделка космопорта: что и сколько игрок набирает на странице продажи или закупки. С уходом со страницы бросается.
  const [deal, setDeal] = useState<Deal | null>(null)
  useEffect(() => setDeal(null), [selectionKey, page])
  // Подарок союзнику набирается по страницам подряд, поэтому живёт, пока выбран тот же космопорт.
  const [ferry, setFerry] = useState<FerryDraft | null>(null)
  useEffect(() => setFerry(null), [selectionKey])
  const { trade } = state
  // Союзники — справа над панелью, всегда на виду: у кого союз, тот и в списке.
  const allies = state.players.filter(({ own, relation }) => !own && relation === 'ally')
  // Своя строка: по ней кнопки союза понимают, глава мы, рядовой или одиночка, см. allyActions.
  const self = state.players.find(({ own }) => own)
  const dealing = trade && !trade.order && deal && (page === 'sell' || page === 'buy') ? dealOf(trade, state.credits, page === 'buy', deal) : null

  const slots = selected ? commandsOf(state, page, { send, place, pave, route, serve, patrol, open: setPage, pick: setDeal, draft: setFerry }, deal, ferry) : []

  // Клавиши сетки. Читаются из ref, чтобы не переподписываться на каждое обновление.
  const slotsRef = useRef(slots)
  slotsRef.current = slots
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLInputElement || event.altKey || event.ctrlKey || event.metaKey || event.repeat) return
      const i = (GRID_KEYS as readonly string[]).indexOf(event.code)
      const slot = i >= 0 ? slotsRef.current[i] : null
      if (!slot || slot.disabled) return
      event.preventDefault()
      slot.run()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Список игроков сервера — пока зажат Tab. Отпустив Tab, он остаётся, пока мышь на нём: так видно,
  // что кнопки союза не исчезают из-под руки.
  const [roster, setRoster] = useState(false)
  const overRoster = useRef(false)
  useEffect(() => {
    const toggle = (shown: boolean) => (event: KeyboardEvent) => {
      if (event.code !== 'Tab' || event.target instanceof HTMLInputElement) return
      event.preventDefault()
      setRoster(shown || overRoster.current)
    }
    const down = toggle(true)
    const up = toggle(false)
    const hide = () => setRoster(false)
    window.addEventListener('keydown', down)
    window.addEventListener('keyup', up)
    window.addEventListener('blur', hide)
    return () => {
      window.removeEventListener('keydown', down)
      window.removeEventListener('keyup', up)
      window.removeEventListener('blur', hide)
    }
  }, [])

  return (
    <>
      {roster && state.players.length > 0 && (
        <section
          class="hud roster"
          aria-label={t('hud.players')}
          onMouseEnter={() => (overRoster.current = true)}
          onMouseLeave={() => (overRoster.current = false)}
        >
          <h2>{t('hud.roster', { online: state.players.filter(({ online }) => online).length })}</h2>
          <ul>
            {state.players.map((row) => {
              const acts = allyActions(row, self)
              return (
                <li key={row.player} class={row.own ? 'is-own' : row.online ? undefined : 'is-away'}>
                  <span>
                    {row.name}
                    {row.safe && <em>{t('hud.sheltered')}</em>}
                    {row.head === row.player && <em class="is-ally">{t('hud.allyHead')}</em>}
                    {row.relation && <em class={row.relation === 'ally' ? 'is-ally' : undefined}>{t(`hud.${row.relation}`)}</em>}
                  </span>
                  {acts.length > 0 && (row.own || row.online) && (
                    <span class="roster__acts">
                      {acts.map(({ label, command }) => (
                        <button key={label} onClick={() => say(command)}>
                          {t(label)}
                        </button>
                      ))}
                    </span>
                  )}
                  <small>{row.own ? t('hud.you') : row.online ? t('hud.online') : t('hud.offline')}</small>
                </li>
              )
            })}
          </ul>
        </section>
      )}
      <header class="hud hud--top">
        <span class="hud--credits" data-tip={t('common.credits')}>
          <span class="hud__coin" />
          {state.credits}
          {state.income > 0 && <small>{t('hud.perSecond', { amount: state.income })}</small>}
        </span>
        {state.protection && (
          <span class="hud__shelter" data-tip={t('hud.protectionTip')}>
            {t('hud.protection', { left: state.protection })}
          </span>
        )}
        {state.stock && (
          <span class="hud__stock" data-tip={t('hud.stockTip', { capacity: state.stock.capacity })}>
            <Stacks items={state.stock.items} />
            {state.stock.items.length === 0 && <span class="hud__res">{t('hud.storesEmpty')}</span>}
          </span>
        )}
        <span class="hud__clock" data-tip={state.storm ? t('hud.clockStorm') : t('hud.clock')}>
          {state.clock}
          {state.storm && t('hud.storm')}
        </span>
        <span class="hud__menu">{menu}</span>
      </header>

      {allies.length > 0 && (
        <section class="hud hud--allies" aria-label={t('hud.friends')}>
          {allies.map(({ name, online, credits, base, player, head }) => (
            <button
              key={name}
              class={online ? 'hud__ally' : 'hud__ally is-away'}
              data-tip={t('hud.friendTip', { name, credits })}
              onClick={() => base && flyTo(base.x, base.y)}
            >
              <em class="is-ally" />
              {name}
              {head === player && <b>{t('hud.allyHead')}</b>}
              <small>{credits}</small>
            </button>
          ))}
        </section>
      )}

      <Tip />
      {state.chat && <Chat
          lines={state.chat}
          picking={state.picking}
          commands={state.commands}
          admin={state.admin}
          names={state.players.map((player) => player.name)}
          say={say}
        />}
      {state.hover && (
        <div class="hud tip" style={{ left: `${state.hover.x + 16}px`, top: `${state.hover.y + 16}px` }}>
          <Res resource={state.hover.kind} /> {t('hud.depositTip', { name: t(`goods.${state.hover.kind}`) })}
          <small>{state.hover.left === null ? t('hud.leftUnknown') : state.hover.left > 0 ? t('hud.left', { amount: state.hover.left }) : t('hud.depleted')}</small>
        </div>
      )}

      {state.pipeHover && (
        <div class="hud tip" style={{ left: `${state.pipeHover.x + 16}px`, top: `${state.pipeHover.y + 16}px` }}>
          {t('hud.network', { buildings: state.pipeHover.buildings })}
          <small>
            {state.pipeHover.stock.length
              ? state.pipeHover.stock.map(({ resource, amount }) => <Res key={resource} resource={resource} amount={amount} />)
              : state.pipeHover.capacity ? t('hud.storesEmpty') : t('hud.noStores')}
          </small>
        </div>
      )}

      {state.alerts.length > 0 && !state.editor && (
        <div class="hud hud--alerts" role="status">
          {state.alerts.map(({ kind, text, x, y, at }) => (
            <button key={at} class={`hud__alert is-${kind}`} data-tip={t('hud.showPlace')} onClick={() => flyTo(x, y)}>
              {t(`hud.alerts.${text}`)}
            </button>
          ))}
        </div>
      )}

      {fresh.length > 0 && (
        <div class="hud hud--rewards">
          {fresh.map((key) => (
            <div key={key} class="hud__reward">
              <span class="hud__coin" />
              <strong>+{REWARDS[key as Reward]}</strong> {t(`rewards.${key as Reward}`)}
            </div>
          ))}
        </div>
      )}

      {/* В редакторе справа его панель: от нижней остаётся только мини-карта. */}
      <section class={`hud bar${state.editor ? ' is-editor' : ''}`}>
        <MinimapView minimap={minimap} lookAt={lookAt} moveSelected={moveSelected} />
        {state.editor ? null : selected ? (
          <Info
            state={state}
            lookAtSelection={lookAtSelection}
            narrow={narrow}
            deal={dealing && { ...dealing, buying: page === 'buy' }}
            ferry={ferry}
            setAmount={(amount) => deal && setDeal({ ...deal, amount })}
            confirm={() => {
              if (!trade || !dealing || dealing.amount < 1) return
              send({ type: page === 'buy' ? 'buy' : 'sell', port: trade.port, resource: dealing.resource, amount: dealing.amount })
              setDeal(null)
            }}
          />
        ) : (
          <div class="bar__info bar__info--empty">
            {/* Подсказка по управлению — по предложению в строке. */}
            {t('hud.nothingSelected')
              .split(/(?<=\.)\s+/)
              .map((line) => (
                <div key={line}>{line}</div>
              ))}
          </div>
        )}
        {!state.editor && <CommandGrid slots={selected ? slots : Array(GRID_KEYS.length).fill(null)} />}
      </section>
    </>
  )
}
