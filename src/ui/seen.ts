/**
 * Кнопки стройки, которые игрок уже нажимал хоть раз: здания и инструменты протяжки. Остальные открытые сетка
 * подсвечивает как новые, а с ними и их разделы, — так игрок видит, что появилось и куда нажать. Хранится в браузере,
 * а не в мире: это знакомство игрока с игрой, и в новой партии знакомое уже не новое.
 */
const KEY = 'kharos.seen'

let seen: Set<string> | null = null

function load() {
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) ?? '[]') as unknown
    return new Set(Array.isArray(saved) ? saved.filter((item): item is string => typeof item === 'string') : [])
  } catch {
    return new Set<string>()
  }
}

/** Нажимал ли игрок эту кнопку: вид здания или инструмент протяжки. */
export function isSeen(key: string) {
  seen ??= load()
  return seen.has(key)
}

/** Игрок нажал кнопку: дальше она не новая. */
export function markSeen(key: string) {
  seen ??= load()
  if (seen.has(key)) return
  seen.add(key)
  try {
    localStorage.setItem(KEY, JSON.stringify([...seen]))
  } catch {
    // Хранилище недоступно: знакомое помнится до перезагрузки.
  }
}
