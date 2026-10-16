/**
 * Описания типов API модов для редактора мододела — по схеме packages/mods/src/api.ts: mods/types/kharos.d.luau (luau-lsp,
 * «definitionFiles») и mods/types/kharos.d.ts (для будущей машины TS/JS). Схема поменялась — запустить заново;
 * tests/mods.test.ts сверяет файлы со схемой.
 *
 * Запуск: bun tools/modapi.ts
 */
import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { MOD_API, luauDefinitions, tsDefinitions } from '@kharos/mods/api'

const TYPES = join(import.meta.dir, '../mods/types')
writeFileSync(join(TYPES, 'kharos.d.luau'), luauDefinitions(MOD_API))
writeFileSync(join(TYPES, 'kharos.d.ts'), tsDefinitions(MOD_API))
console.log(`API модов, версия ${MOD_API.version}: mods/types/kharos.d.luau, mods/types/kharos.d.ts`)
