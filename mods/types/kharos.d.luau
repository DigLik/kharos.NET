-- Создано tools/modapi.ts по src/mods/api.ts, не править. API модов, версия 1.

type Entity = number
type Player = number

--- Здание достроено: стройка кончилась, и оно заработало.
type BuiltEvent = {
	--- Здание.
	entity: Entity,
	--- Чьё оно.
	player: Player,
	--- Вид здания — ключ таблицы зданий, например "mine".
	building: string,
	--- Левый верхний тайл здания.
	x: number,
	--- Левый верхний тайл здания.
	y: number,
}

declare kharos: {
	--- Подписывает обработчик на событие.
	on: ((event: "built", handler: (event: BuiltEvent) -> ()) -> ()),
	--- Пишет всем в чат от имени сервера.
	say: (text: string) -> (),
	--- Ник игрока.
	playerName: (player: Player) -> string?,
}
