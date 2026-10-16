/**
 * API модов — одна схема на все машины (сейчас Luau, потом и TS/JS): события, на которые мод подписывается, и
 * функции таблицы kharos. По схеме хост проверяет аргументы (runtime.ts), а `bun tools/modapi.ts` пишет описания
 * типов для редактора мододела: packages/mods/types/kharos.d.luau (luau-lsp) и
 * packages/mods/types/kharos.d.ts.
 *
 * Правила схемы — чтобы API не зависело от языка: сущности и игроки — номерами, данные — как в JSON, у функции не
 * больше одного результата, обратные вызовы — только подписками на события. Ни корутин, ни нескольких результатов,
 * ни дыр в массивах.
 */

/** Типы значений API. entity и player — целые номера, отдельными типами — для описаний и проверки. */
export type ApiType = 'number' | 'integer' | 'string' | 'boolean' | 'entity' | 'player'

export interface ApiField {
  type: ApiType
  /** Может не быть (nil). */
  optional?: boolean
  doc: string
}

export interface ApiParam extends ApiField {
  name: string
}

export interface ApiEvent {
  doc: string
  fields: Readonly<Record<string, ApiField>>
}

export interface ApiFunction {
  doc: string
  params: readonly ApiParam[]
  returns?: ApiField
}

export interface ApiSchema {
  /** Версия API: растёт, когда меняется уже объявленное. Новое — без смены версии. */
  version: number
  events: Readonly<Record<string, ApiEvent>>
  functions: Readonly<Record<string, ApiFunction>>
}

export const MOD_API = {
  version: 1,
  events: {
    built: {
      doc: 'Здание достроено: стройка кончилась, и оно заработало.',
      fields: {
        entity: { type: 'entity', doc: 'Здание.' },
        player: { type: 'player', doc: 'Чьё оно.' },
        building: { type: 'string', doc: 'Вид здания — ключ таблицы зданий, например "mine".' },
        x: { type: 'integer', doc: 'Левый верхний тайл здания.' },
        y: { type: 'integer', doc: 'Левый верхний тайл здания.' },
      },
    },
  },
  functions: {
    say: {
      doc: 'Пишет всем в чат от имени сервера.',
      params: [{ name: 'text', type: 'string', doc: 'Текст; длинный обрезается, как сообщение игрока.' }],
    },
    playerName: {
      doc: 'Ник игрока.',
      params: [{ name: 'player', type: 'player', doc: 'Номер игрока.' }],
      returns: { type: 'string', optional: true, doc: 'Ник; игрока нет — nil.' },
    },
  },
} as const satisfies ApiSchema

export type ModApi = typeof MOD_API

type ValueOf<T extends ApiType> = T extends 'string' ? string : T extends 'boolean' ? boolean : number
type FieldValue<F extends ApiField> = ValueOf<F['type']> | (F['optional'] extends true ? undefined : never)
type Params<P extends readonly ApiParam[]> = { [K in keyof P]: P[K] extends ApiParam ? FieldValue<P[K]> : never }

type Fields<F extends Readonly<Record<string, ApiField>>> = { [K in keyof F]: FieldValue<F[K]> }

/** Данные события name, как их отдаёт симуляция. */
export type ModEvent<Name extends keyof ModApi['events']> = Fields<ModApi['events'][Name]['fields']>

/** Что хост даёт модам: по функции на каждую из схемы, с её аргументами. */
export type ModFunctions = {
  [K in keyof ModApi['functions']]: (
    ...args: Params<ModApi['functions'][K]['params']>
  ) => ModApi['functions'][K] extends { returns: ApiField } ? FieldValue<ModApi['functions'][K]['returns']> : void
}

const LUAU_TYPES: Record<ApiType, string> = { number: 'number', integer: 'number', string: 'string', boolean: 'boolean', entity: 'Entity', player: 'Player' }
const TS_TYPES: Record<ApiType, string> = { number: 'number', integer: 'number', string: 'string', boolean: 'boolean', entity: 'Entity', player: 'Player' }

const capital = (name: string) => name[0].toUpperCase() + name.slice(1)
const eventType = (name: string) => `${capital(name)}Event`
const comment = (prefix: string, doc: string) => doc.split('\n').map((line) => `${prefix}${line}`)

/** Описание типов для luau-lsp: файл определений (`declare`). */
export function luauDefinitions(schema: ApiSchema): string {
  const lines = [`-- Создано tools/modapi.ts по packages/mods/src/api.ts, не править. API модов, версия ${schema.version}.`, '', 'type Entity = number', 'type Player = number', '']
  const luau = (field: ApiField) => LUAU_TYPES[field.type] + (field.optional ? '?' : '')
  for (const [name, event] of Object.entries(schema.events)) {
    lines.push(...comment('--- ', event.doc), `type ${eventType(name)} = {`)
    for (const [field, spec] of Object.entries(event.fields)) lines.push(...comment('\t--- ', spec.doc), `\t${field}: ${luau(spec)},`)
    lines.push('}', '')
  }
  const members: string[] = []
  const on = Object.keys(schema.events).map((name) => `((event: "${name}", handler: (event: ${eventType(name)}) -> ()) -> ())`)
  members.push('\t--- Подписывает обработчик на событие.', `\ton: ${on.join(' & ') || '(event: string, handler: () -> ()) -> ()'},`)
  for (const [name, fn] of Object.entries(schema.functions)) {
    const params = fn.params.map((param) => `${param.name}: ${luau(param)}`).join(', ')
    members.push(...comment('\t--- ', fn.doc), `\t${name}: (${params}) -> ${fn.returns ? luau(fn.returns) : '()'},`)
  }
  lines.push('declare kharos: {', ...members, '}', '')
  return lines.join('\n')
}

/** Описание типов для TypeScript: пространство имён kharos. */
export function tsDefinitions(schema: ApiSchema): string {
  const lines = [`// Создано tools/modapi.ts по packages/mods/src/api.ts, не править. API модов, версия ${schema.version}.`, '', 'declare namespace kharos {', '  type Entity = number', '  type Player = number', '']
  const ts = (field: ApiField) => TS_TYPES[field.type] + (field.optional ? ' | undefined' : '')
  for (const [name, event] of Object.entries(schema.events)) {
    lines.push(`  /** ${event.doc} */`, `  interface ${eventType(name)} {`)
    for (const [field, spec] of Object.entries(event.fields)) lines.push(`    /** ${spec.doc} */`, `    readonly ${field}: ${ts(spec)}`)
    lines.push('  }', '')
  }
  lines.push('  interface Events {', ...Object.keys(schema.events).map((name) => `    ${name}: ${eventType(name)}`), '  }', '')
  lines.push('  /** Подписывает обработчик на событие. */', '  function on<K extends keyof Events>(event: K, handler: (event: Events[K]) => void): void')
  for (const [name, fn] of Object.entries(schema.functions)) {
    const params = fn.params.map((param) => `${param.name}: ${ts(param)}`).join(', ')
    lines.push(`  /** ${fn.doc} */`, `  function ${name}(${params}): ${fn.returns ? ts(fn.returns) : 'void'}`)
  }
  lines.push('}', '')
  return lines.join('\n')
}
