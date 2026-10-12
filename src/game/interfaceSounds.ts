import type { Audio } from '../audio/audio'
import type { SoundName } from '../audio/sounds'
import type { Entity } from '../ecs'
import { Unit, resolveOrder, type Command, type Sim } from '../sim'

/** Громкость звуков интерфейса: тише боя, но слышна поверх него. */
const VOLUME = 0.35
const HOVER_VOLUME = 0.12
/** Чаще этого, в секундах, выделение не звучит: протянутая рамка меняет его каждый кадр. */
const SELECT_GAP = 0.12

/** Какие приказы как звучат; остальные команды — молча: их и так сопровождает щелчок кнопки. */
const ORDER_SOUNDS: Partial<Record<Command['type'], SoundName>> = {
  move: 'order',
  patrol: 'order',
  route: 'order',
  serve: 'order',
  pickup: 'order',
  haul: 'order',
  assist: 'order',
  attack: 'attackOrder',
  rally: 'place',
  build: 'place',
  pave: 'place',
  unpave: 'place',
  pipes: 'place',
  walls: 'place',
  wells: 'place',
}

/** Событие окна: нажата клавиша сетки команд. Сетка шлёт его, звук слушает — так им не нужно знать друг о друге. */
export const GRID_PRESS = 'kharos:grid-press'
/** Событие окна: игрок пытался сделать нельзя — например, строить на запретном месте. */
export const DENIED = 'kharos:denied'
export interface GridPress {
  disabled: boolean
  /** Клавиша открывает раздел, а не действие. */
  group: boolean
}

/**
 * Звуки интерфейса: щелчок по кнопке, касание кнопки при наведении, вход в раздел, отказ у недоступной кнопки,
 * подтверждение приказов и выделение юнитов и зданий. Кнопки слушаются одним обработчиком на документе — компонентам
 * не нужно о них знать; приказы — обёрткой отправки команд игрока; выделение — сверкой раз в кадр.
 */
export function createInterfaceSounds(sim: Sim, audio: Audio) {
  const play = (name: SoundName, volume = VOLUME) => audio.play(name, volume, 0)

  const button = (target: EventTarget | null) => (target instanceof Element ? target.closest<HTMLButtonElement>('.hud button, .game button') : null)
  const down = (event: PointerEvent) => {
    const pressed = button(event.target)
    if (!pressed) return
    if (pressed.disabled) play('deny')
    else play(pressed.classList.contains('bar__cell--group') ? 'open' : 'click')
  }
  let hovered: Element | null = null
  const over = (event: PointerEvent) => {
    // Касание — у любой кнопки интерфейса, как в меню: у сетки команд, верхней полосы, ленты уведомлений.
    const cell = button(event.target)
    if (cell === hovered) return
    hovered = cell
    if (cell && !cell.disabled) play('hover', HOVER_VOLUME)
  }
  const denied = () => play('deny')
  const key = (event: Event) => {
    const { disabled, group } = (event as CustomEvent<GridPress>).detail
    play(disabled ? 'deny' : group ? 'open' : 'click')
  }
  document.addEventListener('pointerdown', down, true)
  document.addEventListener('pointerover', over)
  window.addEventListener(GRID_PRESS, key)
  window.addEventListener(DENIED, denied)

  // Приказ звучит, когда уходит хосту: так одинаково для мыши, горячих клавиш и мини-карты.
  const send = sim.send.bind(sim)
  // Приказ точкой звучит тем, во что он выльется: по врагу — атакой, по стройке — закладкой, иначе — движением.
  sim.send = (player, command) => {
    let sound = ORDER_SOUNDS[command.type]
    if (command.type === 'order') sound = ORDER_SOUNDS[resolveOrder(sim, player, command.units as Entity[], command, undefined, false)[0]?.type ?? 'move'] ?? 'order'
    if (sound) play(sound)
    return send(player, command)
  }

  let selected = ''
  let clock = 0
  let selectedAt = -Infinity
  return {
    /** Раз в кадр: выделение сменилось и в нём что-то есть — щелчок, у юнитов один, у здания другой. Снятое молчит. */
    update(seconds: number, selection: ReadonlySet<Entity>) {
      clock += seconds
      const key = [...selection].join(',')
      if (key === selected) return
      selected = key
      if (!selection.size || clock - selectedAt < SELECT_GAP) return
      selectedAt = clock
      play([...selection].some((entity) => sim.world.has(entity, Unit)) ? 'select' : 'selectBuilding')
    },
    destroy() {
      document.removeEventListener('pointerdown', down, true)
      document.removeEventListener('pointerover', over)
      window.removeEventListener(GRID_PRESS, key)
      window.removeEventListener(DENIED, denied)
      sim.send = send
    },
  }
}
