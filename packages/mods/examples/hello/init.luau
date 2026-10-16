--!strict
-- Пример мода: сервер поздравляет в чате с каждым достроенным зданием.

local built = 0

kharos.on("built", function(event)
	built += 1
	local name = kharos.playerName(event.player) or "кто-то"
	kharos.say(`{name} достроил {event.building} — {built}-е здание с запуска сервера`)
end)

print("hello загружен")
