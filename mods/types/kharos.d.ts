// Создано tools/modapi.ts по packages/mods/src/api.ts, не править. API модов, версия 1.

declare namespace kharos {
  type Entity = number
  type Player = number

  /** Здание достроено: стройка кончилась, и оно заработало. */
  interface BuiltEvent {
    /** Здание. */
    readonly entity: Entity
    /** Чьё оно. */
    readonly player: Player
    /** Вид здания — ключ таблицы зданий, например "mine". */
    readonly building: string
    /** Левый верхний тайл здания. */
    readonly x: number
    /** Левый верхний тайл здания. */
    readonly y: number
  }

  interface Events {
    built: BuiltEvent
  }

  /** Подписывает обработчик на событие. */
  function on<K extends keyof Events>(event: K, handler: (event: Events[K]) => void): void
  /** Пишет всем в чат от имени сервера. */
  function say(text: string): void
  /** Ник игрока. */
  function playerName(player: Player): string | undefined
}
