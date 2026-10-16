import { DEFAULT_CONFIG, type GeneratorConfig } from '@kharos/shared/terrain'
import { DEFAULT_LIMITS, type ConnectLimits } from '../src/net/limits'
import type { Lang } from '@kharos/i18n'
import { DEFAULT_SEND_RATE } from '../src/net/host'
import { DEFAULT_PORT } from '../src/net/protocol'
import { DEFAULT_RULES, type Rules } from '@kharos/sim-ts'
import { DEFAULT_WEATHER, type WeatherOptions } from '@kharos/sim-ts/weather'
import { DEFAULT_TLS, type TlsSettings } from './tls'

/** Настройки сервера из settings.json. */
export interface ServerSettings {
  port: number
  /** Язык самого сервера: ответы в его консоли, «Игрок N» и подпись /say. Сообщения в чате каждый игрок читает на своём. */
  lang: Lang
  /** Пароль на вход; пустой — заходит кто угодно. */
  password: string
  /** Пароль режима администратора, /admin в чате; пустой — режима нет. */
  admin: string
  /** Файл сохранения мира, двоичный, см. src/save/file.ts. */
  save: string
  /**
   * Сторона карты в тайлах. Как и generator, fog и weather, действует только на новый мир. Меньше 1024 при правилах
   * сессии брать не стоит: на 256 харита не хватает на одно дерево исследований.
   */
  size: number
  /** Туман войны. */
  fog: boolean
  generator: GeneratorConfig
  weather: WeatherOptions
  /** Правила игры. В отличие от остального применяются и к миру из сохранения. */
  rules: Rules
  /** wss: off, auto (Let's Encrypt) или files (свои сертификат и ключ). */
  tls: TlsSettings
  /** Ограничения на подключения с одного адреса; ноль снимает ограничение. */
  limits: ConnectLimits
  /**
   * Сколько КБ в секунду сервер шлёт одному игроку: в большом бою то, что не влезло, приходит реже — сначала то, что
   * у игрока на экране, см. рассылку в src/net/host.ts. Ноль снимает ограничение.
   */
  sendRate: number
  /**
   * Моды: папки, в каждой — init.luau (см. packages/mods). Путь — от рабочей папки сервера. Код модов выполняется на
   * сервере в песочнице, игрокам ничего не ставится.
   */
  mods: string[]
}

/**
 * Правила серверной сессии, которыми она отличается от матча. Мир живёт две недели, и дерево исследований
 * растянуто на них ростом цены по ярусам сложности: каждый ярус втрое дороже и дольше прошлого. Первая тема — та же,
 * что в матче (100 кредитов и 10 руды): новичку она по силам; последняя — 24 300 кредитов. Всё дерево — 83 тысячи
 * кредитов и около тысячи компонентов (харит с трёх-четырёх месторождений) и девятисот стержней. Срок растянут ещё
 * вдесятеро: первая тема — 3 минуты одной лаборатории, последняя — 13,5 часа, всё дерево — 46 часов. Лаборатории
 * складывают скорость, но не цену: сроки сокращает тот, у кого хватает и сырья, и кредитов.
 * Уровни зданий впятеро дороже матчевых: поднять типовую базу до третьего уровня — 42 тысячи кредитов, 2900
 * стройблоков и 500 компонентов. Замеры — `bun run balance research`.
 */
export const SESSION_RULES: Partial<Rules> = { researchScale: 1, researchGrowth: 3, researchSlow: 10, upgradeScale: 5 }

/** Настройки по умолчанию; seed у каждого нового сервера свой. */
export const defaultSettings = (): ServerSettings => ({
  port: DEFAULT_PORT,
  lang: 'ru',
  password: '',
  admin: '',
  save: 'save.kharos',
  size: 1024,
  fog: true,
  generator: { ...DEFAULT_CONFIG, seed: Math.floor(Math.random() * 2 ** 31) },
  weather: { ...DEFAULT_WEATHER },
  rules: { ...DEFAULT_RULES, ...SESSION_RULES },
  tls: { ...DEFAULT_TLS },
  limits: { ...DEFAULT_LIMITS },
  sendRate: DEFAULT_SEND_RATE,
  mods: [],
})

/**
 * Накладывает прочитанное из файла на значения по умолчанию. Берёт только известные ключи того же типа, что
 * и по умолчанию; про остальное пишет в warnings, чтобы опечатка не пропала молча.
 */
export function mergeSettings(defaults: ServerSettings, source: unknown, warnings: string[] = []): ServerSettings {
  const merge = <T extends object>(base: T, value: unknown, path: string): T => {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
      warnings.push(`${path || 'настройки'}: ожидался объект`)
      return base
    }
    const result = { ...base } as Record<string, unknown>
    for (const [key, item] of Object.entries(value)) {
      const name = path ? `${path}.${key}` : key
      if (!(key in base)) warnings.push(`${name}: неизвестный параметр`)
      // Списки в настройках — строк.
      else if (Array.isArray(result[key])) {
        if (Array.isArray(item) && item.every((entry) => typeof entry === 'string')) result[key] = item
        else warnings.push(`${name}: ожидался список строк`)
      } else if (typeof result[key] === 'object') result[key] = merge(result[key] as object, item, name)
      else if (typeof item !== typeof result[key] || (typeof item === 'number' && !Number.isFinite(item))) warnings.push(`${name}: ожидалось ${typeof result[key]}`)
      else result[key] = item
    }
    return result as T
  }
  return merge(defaults, source, '')
}
