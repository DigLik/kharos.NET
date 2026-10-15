// Чат на холсте, как в Minecraft: Enter (или «/») открывает строку ввода, Enter отправляет, Escape закрывает. Закрытый
// чат показывает свежие сообщения, и через CHAT_SHOWN секунд они гаснут; открытый — всю историю с прокруткой.
// Над строкой — подсказки дополнения команд и ников: Tab подставляет, стрелки выбирают.

import type { HudState } from '../game/hud'
import { complete } from '../net/chatCommands'
import { t } from '../i18n'
import type { Ui } from './ui'

/** Сколько секунд сообщение видно, пока чат закрыт, и сколько из них оно гаснет. */
const CHAT_SHOWN = 10
const CHAT_FADE = 1
/** Сколько последних сообщений видно в закрытом чате. */
const CHAT_RECENT = 8
/** Сколько подсказок дополнения видно над строкой. */
const CHAT_SUGGESTIONS = 6
const WIDTH = 480

/** Цвета чата — как у Minecraft, а не как у остального интерфейса: так его узнают. */
const WHITE = '#ffffff'
const SHADE = '#3f3f3f'
const SYSTEM = '#ffff55'
const SYSTEM_SHADE = '#3f3f15'
const WHISPER = '#aaaaaa'
const OWN = '#ffe066'

export interface Chat {
  draw(ui: Ui, state: HudState, left: number, bottom: number): void
  destroy(): void
}

/** Чат: открывается клавишей, пока в поле ничего не набирают и на карте ничего не выбирают. */
export function createChat(say: (text: string) => void, typing: () => boolean): Chat {
  let open = false
  /** Только что открыт: строка в этом кадре берёт фокус. */
  let opening = false
  let draft = ''
  /** Строка спрашивает пароль администратора: набранное скрыто и уходит как /admin <пароль>. */
  let password = false
  /** Какая подсказка дополнения выбрана стрелками. */
  let chosen = 0
  let picking = false

  const onKey = (event: KeyboardEvent) => {
    if (open || typing() || event.repeat || picking) return
    // «/» открывает чат сразу с началом команды; на русской раскладке та же клавиша — точка.
    const slash = event.key === '/' || event.code === 'Slash' || event.code === 'NumpadDivide'
    if (event.code !== 'Enter' && event.code !== 'NumpadEnter' && !slash) return
    event.preventDefault()
    draft = slash ? '/' : ''
    open = opening = true
  }
  window.addEventListener('keydown', onKey)

  const close = () => {
    open = false
    draft = ''
    password = false
    chosen = 0
  }

  return {
    draw(ui, state, left, bottom) {
      if (!state.chat) return
      picking = state.picking
      const { chat: lines, commands, admin } = state
      const names = state.players.map((player) => player.name)
      const width = Math.min(WIDTH, ui.width - 24)
      const now = ui.now
      const shown = open ? lines : lines.slice(-CHAT_RECENT).filter((line) => now - line.at < CHAT_SHOWN * 1000)
      if (!open && !shown.length) return
      const suggestions = password ? [] : complete(draft, commands, names).slice(0, CHAT_SUGGESTIONS)
      const pick = Math.min(chosen, suggestions.length - 1)
      // Закрытый чат не держит мышь: сквозь него щёлкают по карте.
      ui.box({ id: 'chat', anchor: { left, bottom }, width, direction: 'column', passive: !open }, () => {
        ui.box({ id: 'chat:lines', direction: 'column', maxHeight: Math.round(ui.height * 0.4), scroll: open ? 'end' : false, clip: true, align: 'stretch' }, () => {
          for (const line of shown) {
            const fade = open ? 1 : Math.min(1, (CHAT_SHOWN - (now - line.at) / 1000) / CHAT_FADE)
            ui.box({ background: 'rgb(0 0 0 / 0.45)', padding: [1, 6], alpha: fade }, () => {
              const style = { width: width - 12, lift: 2 }
              if (line.system) ui.flow([line.name ? `${line.name} ${line.text}` : line.text], { ...style, color: SYSTEM, shadow: SYSTEM_SHADE })
              else if (line.whisper) ui.flow([`<${line.name} → ${line.whisper}> ${line.text}`], { ...style, color: WHISPER, shadow: SHADE })
              else ui.flow([{ text: `<${line.name}>`, color: line.own ? OWN : WHITE }, ` ${line.text}`], { ...style, color: WHITE, shadow: SHADE })
            })
          }
          // Гаснущее перерисовывается само: кадр нужен, даже если ничего не пришло.
        })
        if (!open) return
        if (suggestions.length) {
          ui.box({ direction: 'column', align: 'stretch', padding: [2, 0, 0, 0] }, () => {
            suggestions.forEach((item, i) => {
              const id = `chat:suggestion:${i}`
              ui.box({ id, keepFocus: true, background: 'rgb(0 0 0 / 0.7)', padding: [1, 6], direction: 'row', gap: 6 }, () => {
                ui.text(item.label, { color: i === pick ? SYSTEM : WHITE })
                ui.text(item.help, { color: WHISPER })
              })
              if (ui.clicked(id)) {
                draft = item.text
                chosen = 0
              }
            })
          })
        }
        const field = ui.input('chat:input', draft, {
          width,
          password,
          placeholder: password ? t('hud.adminPassword') : admin ? t('hud.adminMode') : undefined,
          maxLength: 200,
          focus: opening,
          background: 'rgb(0 0 0 / 0.6)',
          outline: admin ? '#ff5555' : undefined,
          color: WHITE,
        })
        opening = false
        if (field.value !== draft) {
          draft = field.value
          chosen = 0
        }
        for (const key of field.keys) {
          if (key === 'Escape' || key === 'Blur') close()
          else if (key === 'Tab' && suggestions[pick]) {
            draft = suggestions[pick].text
            chosen = 0
          } else if ((key === 'ArrowUp' || key === 'ArrowDown') && suggestions.length) {
            chosen = (pick + (key === 'ArrowUp' ? -1 : 1) + suggestions.length) % suggestions.length
          } else if (key === 'Enter') {
            // Команда с паролем (/admin) спрашивает его отдельно, скрытым полем: в строке чата его видно.
            const secret = commands.find((command) => command.secret && draft.trim().toLowerCase().split(' ')[0] === `/${command.name}`)
            if (!password && secret) {
              password = true
              draft = ''
              continue
            }
            if (password) {
              if (draft) say(`/admin ${draft}`)
            } else if (draft.trim()) say(draft)
            close()
          }
        }
      })
    },
    destroy() {
      window.removeEventListener('keydown', onKey)
    },
  }
}
