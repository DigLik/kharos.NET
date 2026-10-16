/**
 * Ресурсы, общие на контекст: единичный квадрат, шейдерные программы, атласы. Контекст на странице один на все
 * игры (см. stage.ts), и то, что не зависит от мира, создаётся на видеокарте один раз, а не с каждой игрой.
 */
const stores = new WeakMap<WebGL2RenderingContext, Map<unknown, unknown>>()

/** Общий ресурс контекста под ключом key; создаётся при первом обращении. Уничтожать его нельзя: им пользуются все. */
export function shared<T>(gl: WebGL2RenderingContext, key: unknown, create: () => T): T {
  let store = stores.get(gl)
  if (!store) stores.set(gl, (store = new Map()))
  if (!store.has(key)) store.set(key, create())
  return store.get(key) as T
}

/** Есть ли уже общий ресурс под этим ключом. */
export function isShared(gl: WebGL2RenderingContext, key: unknown) {
  return stores.get(gl)?.has(key) ?? false
}

/** Забывает общие ресурсы контекста. Вызывать после его восстановления: старые буферы, программы и текстуры больше не действуют. */
export function forgetContext(gl: WebGL2RenderingContext) {
  stores.delete(gl)
}
