/**
 * Сборка машины модов: Luau закреплённой версии и привязка packages/mods/src/luau.cpp → packages/mods/src/luau.wasm.
 *
 * Что делает:
 * - качает Luau тега LUAU_TAG в .cache/luau (повторный запуск не качает; другой тег — папку удалить);
 * - компилирует нужные части (Common, Ast, Bytecode, Compiler, VM — без анализатора типов и JIT) в .cache/luau-build,
 *   параллельно и только изменившееся;
 * - собирает самостоятельный wasm без JS-обвязки Emscripten: его импорты — часы, вызов хоста и print (модуль kharos),
 *   остальное заглушает packages/mods/src/luau.ts.
 *
 * Ошибки Luau — исключения C++, они идут родными исключениями wasm (-fwasm-exceptions): быстрее обвязки на JS, и их
 * умеют все наши хосты — Chrome, Firefox, Safari, Bun и V8 нативной оболочки. Стек — первым в памяти и в 1 МБ:
 * переполнить его глубокой вложенностью кода — ловушка wasm (машина ломается), а не порча кучи.
 *
 * Запуск: bun tools/luau.ts (нужен Emscripten: brew install emscripten).
 */
import { $ } from 'bun'
import { existsSync, mkdirSync, readdirSync, statSync } from 'node:fs'
import { availableParallelism } from 'node:os'
import { join } from 'node:path'

const LUAU_TAG = '0.742'
const ROOT = join(import.meta.dir, '..')
const LUAU = join(ROOT, '.cache/luau')
const BUILD = join(ROOT, '.cache/luau-build')
const GLUE = join(ROOT, 'packages/mods/src/luau.cpp')
const OUT = join(ROOT, 'packages/mods/src/luau.wasm')
const PARTS = ['Common', 'Ast', 'Bytecode', 'Compiler', 'VM']

const COMPILE = ['-O2', '-std=c++17', '-fwasm-exceptions', '-DNDEBUG', ...PARTS.map((part) => `-I${join(LUAU, part, 'include')}`)]
const LINK = [
  '-O2',
  '-fwasm-exceptions',
  '--no-entry',
  '-sSTANDALONE_WASM',
  '-sALLOW_MEMORY_GROWTH',
  '-sINITIAL_MEMORY=4MB',
  '-sMAXIMUM_MEMORY=256MB',
  '-sSTACK_SIZE=1MB',
  '-sERROR_ON_UNDEFINED_SYMBOLS=0',
  '-Wl,--stack-first',
]

if (!existsSync(LUAU)) await $`git clone --depth 1 --branch ${LUAU_TAG} https://github.com/luau-lang/luau.git ${LUAU}`
mkdirSync(BUILD, { recursive: true })

const sources = [...PARTS.flatMap((part) => readdirSync(join(LUAU, part, 'src')).filter((name) => name.endsWith('.cpp')).map((name) => join(LUAU, part, 'src', name))), GLUE]
const objectOf = (source: string) => join(BUILD, source.slice(ROOT.length + 1).replaceAll('/', '_').replace(/\.cpp$/, '.o'))
const stale = sources.filter((source) => !existsSync(objectOf(source)) || statSync(objectOf(source)).mtimeMs < statSync(source).mtimeMs)

console.log(`Luau ${LUAU_TAG}: ${sources.length} файлов, компилировать ${stale.length}`)
const queue = [...stale]
await Promise.all(
  Array.from({ length: availableParallelism() }, async () => {
    for (let source = queue.shift(); source; source = queue.shift()) await $`em++ ${COMPILE} -c ${source} -o ${objectOf(source)}`.quiet()
  }),
)
await $`em++ ${LINK} ${sources.map(objectOf)} -o ${OUT}`
const size = statSync(OUT).size
const packed = Bun.gzipSync(await Bun.file(OUT).bytes()).length
console.log(`${OUT.slice(ROOT.length + 1)}: ${(size / 1024).toFixed(0)} КБ, сжатый gzip — ${(packed / 1024).toFixed(0)} КБ`)
