/**
 * Команды чата: строка, начатая с «/», — не сообщение, а команда хосту. Список команд хост шлёт клиенту сам (у
 * администратора он длиннее), а клиент по нему дописывает команду и её аргументы, см. complete.
 */

import { commandHelp, commandLabel } from '@kharos/i18n'

/** Команда, какой её знает клиент: имя без «/», аргументы и что делает. */
export interface CommandInfo {
  name: string
  /** Аргументы: «<player>» дописывается ником, остальное — подсказка. По сети идут так, а игроку показаны на его языке, см. commandLabel. */
  args: string[]
  /** Что делает команда, если её нет в словаре: команды самого хоста клиент описывает по имени, см. commandHelp. */
  help: string
  /** Чем дописываются аргументы, по их порядку: например, виды юнитов. Аргумент «<player>» дописывается ником и без них. */
  choices?: (readonly string[] | null)[]
  /** Команда ждёт пароль: клиент спрашивает его скрытым полем и шлёт /name <пароль>, в строке чата его не видно. */
  secret?: true
}

/** Аргумент, который дописывается ником игрока. */
export const PLAYER_ARG = '<player>'

/** Слова строки: через пробел, а в двойных кавычках — вместе с пробелами, как ник «Игрок 3». */
export function splitWords(text: string): string[] {
  return [...text.matchAll(/"([^"]*)"?|(\S+)/g)].map((match) => match[1] ?? match[2])
}

/** Ник как аргумент команды: с пробелом — в кавычках. */
export const quoteName = (name: string) => (name.includes(' ') ? `"${name}"` : name)

/**
 * Разбирает строку чата: команда, её аргументы и rest(n) — текст после n аргументов как есть, с кавычками и
 * пробелами, для команд, у которых последний аргумент — сообщение. Не команда — undefined.
 */
export function parseCommand(text: string): { name: string; args: string[]; rest(skip: number): string } | undefined {
  if (!text.startsWith('/')) return undefined
  const [name = '', ...args] = splitWords(text.slice(1))
  const rest = (skip: number) => {
    const pattern = new RegExp(`^\\s*(?:(?:"[^"]*"?|\\S+)\\s+){${skip + 1}}`)
    const match = text.slice(1).match(pattern)
    return match ? text.slice(1 + match[0].length).trim() : ''
  }
  return { name: name.toLowerCase(), args, rest }
}

/** Подсказка дополнения: что подставить в строку целиком и что показать. */
export interface Completion {
  text: string
  label: string
  help: string
}

/** Команда с аргументами, как она показана игроку. */
const labelOf = (command: CommandInfo) => commandLabel([`/${command.name}`, ...command.args].join(' '))

/**
 * Дополнения для набранного: пока набирается имя команды — подходящие команды, дальше — ники для аргумента
 * «<player>». Ник с пробелом дописывается в кавычках.
 */
export function complete(draft: string, commands: readonly CommandInfo[], names: readonly string[]): Completion[] {
  if (!draft.startsWith('/')) return []
  // Последнее слово — то, что дописывается; открытая кавычка значит, что оно ещё набирается.
  const words = [...draft.slice(1).matchAll(/"[^"]*"?|\S+/g)].map((match) => match[0])
  if (draft.endsWith(' ') && (draft.match(/"/g)?.length ?? 0) % 2 === 0) words.push('')
  if (!words.length) words.push('')
  if (words.length === 1) {
    const typed = words[0].toLowerCase()
    return commands
      .filter((command) => command.name.startsWith(typed))
      .map((command) => ({ text: `/${command.name} `, label: labelOf(command), help: commandHelp(command.name, command.help) }))
  }
  const command = commands.find((item) => item.name === words[0].toLowerCase())
  if (!command) return []
  const index = words.length - 2
  const arg = command.args[index]
  const head = draft.slice(0, draft.length - words.at(-1)!.length)
  const typed = words.at(-1)!.replace(/"/g, '').toLowerCase()
  const choices = command.choices?.[index]
  if (choices) {
    return choices.filter((choice) => choice.toLowerCase().startsWith(typed)).map((choice) => ({ text: `${head}${choice} `, label: choice, help: commandLabel(arg ?? '') }))
  }
  if (arg !== PLAYER_ARG) return arg ? [{ text: draft, label: labelOf(command), help: commandHelp(command.name, command.help) }] : []
  return names
    .filter((name) => name.toLowerCase().startsWith(typed))
    .map((name) => ({ text: `${head}${quoteName(name)} `, label: name, help: commandHelp(command.name, command.help) }))
}
