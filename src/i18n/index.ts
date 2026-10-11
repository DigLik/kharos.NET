/**
 * Языки интерфейса. Словарь языка — JSON рядом (ru.json, en.json, …): вложенные разделы со строками, в строке
 * подстановки в фигурных скобках — «{name}», а «{name:lower}» — строчными; нужны ли строчные, язык решает сам
 * (в немецком существительные остаются с заглавной). Словарь не лежит в сборке, а догружается:
 * нужен только выбранный язык. Пока он не загружен, t отдаёт сам ключ, поэтому страница, сервер и тесты первым делом
 * ждут loadLang. Русский словарь — исходный: по нему выводится тип ключей, а остальные обязаны повторить каждую его
 * строку и её подстановки — это проверяет tests/i18n.test.ts.
 */
export type Lang = 'ru' | 'en' | 'es' | 'fr' | 'de' | 'pt' | 'uk'

/** Языки с названиями на них самих: список в настройках читается, пока словарь ещё не загружен. */
export const LANGS: readonly { lang: Lang; name: string }[] = [
  { lang: 'ru', name: 'Русский' },
  { lang: 'en', name: 'English' },
  { lang: 'es', name: 'Español' },
  { lang: 'fr', name: 'Français' },
  { lang: 'de', name: 'Deutsch' },
  { lang: 'pt', name: 'Português' },
  { lang: 'uk', name: 'Українська' },
]

const LOADERS: Record<Lang, () => Promise<{ default: unknown }>> = {
  ru: () => import('./ru.json'),
  en: () => import('./en.json'),
  es: () => import('./es.json'),
  fr: () => import('./fr.json'),
  de: () => import('./de.json'),
  pt: () => import('./pt.json'),
  uk: () => import('./uk.json'),
}

type Dict = typeof import('./ru.json')
type Paths<T, P extends string = ''> = { [K in keyof T & string]: T[K] extends string ? `${P}${K}` : Paths<T[K], `${P}${K}.`> }[keyof T & string]
/** Ключ строки: путь в словаре через точку, «menu.title». */
export type Key = Paths<Dict>
/** Что подставить в строку вместо «{name}». */
export type Params = Record<string, string | number>

const LANG_KEY = 'kharos.lang'
/** Страница, если она есть: в воркере, на сервере и в тестах её нет, и язык там задают явно. */
const page = globalThis as {
  localStorage?: { getItem(key: string): string | null; setItem(key: string, value: string): void }
  navigator?: { languages?: readonly string[] }
  document?: { documentElement: { lang: string } }
}

const isLang = (value: unknown): value is Lang => LANGS.some(({ lang }) => lang === value)

let current: Lang = 'ru'
/** Строки текущего языка по ключу. */
let strings = new Map<string, string>()
const listeners = new Set<() => void>()

const flatten = (dict: object, prefix = '', out = new Map<string, string>()) => {
  for (const [key, value] of Object.entries(dict)) {
    if (typeof value === 'string') out.set(prefix + key, value)
    else if (typeof value === 'object' && value !== null) flatten(value, `${prefix}${key}.`, out)
  }
  return out
}

/** Язык игрока: выбранный раньше, иначе первый подходящий из языков браузера, иначе английский. */
export function preferredLang(): Lang {
  try {
    const saved = page.localStorage?.getItem(LANG_KEY)
    if (isLang(saved)) return saved
  } catch {
    // Хранилище недоступно: язык берётся у браузера.
  }
  for (const tag of page.navigator?.languages ?? []) {
    const lang = tag.toLowerCase().split('-')[0]
    if (isLang(lang)) return lang
  }
  return 'en'
}

export const currentLang = () => current

/** Загружает словарь языка и делает его текущим. Подписчики onLang узнают, что строки поменялись. */
export async function loadLang(lang: Lang): Promise<void> {
  const dict = (await LOADERS[lang]()).default
  strings = flatten(dict as object)
  current = lang
  if (page.document) page.document.documentElement.lang = lang
  for (const listener of [...listeners]) listener()
}

/** Язык по выбору игрока: загружается и запоминается. */
export async function chooseLang(lang: Lang): Promise<void> {
  await loadLang(lang)
  try {
    page.localStorage?.setItem(LANG_KEY, lang)
  } catch {
    // Без хранилища выбор живёт до перезагрузки.
  }
}

/** Зовёт listener, когда язык поменялся; возвращает отписку. */
export function onLang(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/** Есть ли такая строка в словаре: для ключей, собранных из того, что пришло по сети. */
export const hasText = (key: string): key is Key => strings.has(key)

const PLACEHOLDER = /\{(\w+)(?::(\w+))?\}/g

const substitute = (value: unknown, modifier: string | undefined) => (modifier === 'lower' && typeof value === 'string' ? value.toLowerCase() : value)

/** Строка словаря с подстановками. Неизвестный ключ и незагруженный словарь отдают сам ключ. */
export function t(key: Key, params?: Params): string {
  const text = strings.get(key)
  if (text === undefined) return key
  if (!params) return text
  return text.replace(PLACEHOLDER, (whole, name: string, modifier?: string) => (name in params ? String(substitute(params[name], modifier)) : whole))
}

/**
 * Строка словаря, в которую подставляется не только текст: куски строки вперемешку с тем, что передано, — значками
 * грузов. Интерфейс кладёт результат в разметку как есть.
 */
export function rich<N>(key: Key, params: Record<string, string | number | N>): (string | N)[] {
  const text = strings.get(key) ?? key
  const parts: (string | N)[] = []
  let last = 0
  for (const match of text.matchAll(PLACEHOLDER)) {
    const [whole, name, modifier] = match
    if (!(name in params)) continue
    if (match.index > last) parts.push(text.slice(last, match.index))
    const value = substitute(params[name], modifier) as string | number | N
    parts.push(typeof value === 'number' ? String(value) : value)
    last = match.index + whole.length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}

type HostKey = Key extends infer K ? (K extends `host.${infer Id}` ? Id : never) : never

/**
 * Сообщение хоста, которое каждый игрок читает на своём языке: по сети идёт ключ раздела host словаря и подстановки,
 * а не готовый текст.
 */
export type HostText = readonly [id: string, params?: Params]
/** Текст по сети: готовый — то, что написал человек, — или сообщение хоста. */
export type Text = string | HostText

export const hostText = (id: HostKey, params?: Params): HostText => (params ? [id, params] : [id])

/** Аргументы команды чата на языке игрока: «<player>» — «<игрок>». Слова не из словаря остаются как есть. */
export const commandLabel = (label: string) =>
  label.replace(/(?<=[<[|])[a-z]+(?=[>\]|])/gi, (word) => {
    const key = `chat.args.${word}`
    return hasText(key) ? t(key) : word
  })

/** Что делает команда чата: из словаря по её имени, а у команды не из него — как назвал хост. */
export const commandHelp = (name: string, fallback: string) => {
  const key = `chat.help.${name}`
  return hasText(key) ? t(key) : fallback
}

/** Текст сообщения, пришедшего по сети, на текущем языке. */
export function textOf(text: Text): string {
  if (typeof text === 'string') return text
  const [id, params = {}] = text
  const key = `host.${id}`
  if (!hasText(key)) return String(id)
  // В строках о команде её аргументы и описание — тоже на языке игрока.
  const shown = { ...params }
  if (typeof shown.command === 'string') shown.command = commandLabel(shown.command)
  if (typeof shown.name === 'string' && 'help' in shown) shown.help = commandHelp(shown.name, String(shown.help))
  return t(key, shown)
}
