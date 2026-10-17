--!strict
-- Завод: выпускает наземные машины за кредиты. Вид папкой: данные и правила здесь, облик — в look.luau.

local factory = {}

factory.data = {
	title = "Завод",
	-- Размер в тайлах; место — центр здания.
	width = 3,
	height = 3,
	-- Что выпускает: виды из units/.
	produces = { "buggy" },
	-- Куда выходит готовый юнит — от центра, в тайлах.
	exit = { 0, 2.2 },
}

factory.entity = function(kind)
	return {
		Position = {},
		Owner = {},
		Producer = {},
		Building = { width = kind.data.width, height = kind.data.height },
	}
end

factory.look = require("./look")

return factory
