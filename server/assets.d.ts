/** Файлы, которые сборка сервера кладёт рядом или вшивает в бинарник (`with { type: 'file' }`): импорт отдаёт путь. */
declare module '*.wasm' {
  const path: string
  export default path
}
