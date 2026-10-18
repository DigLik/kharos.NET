--!strict
-- Багги: машинка быстрая и хрупкая. За водителем сидит пассажир с миниганом — турель gunner — и стреляет во все
-- стороны. Она же разведчик: видит дальше всех на земле. Данные — из packages/base/data/units.json.

local units = require("lib/units")

local buggy = {}

buggy.data = {
	title = "Багги",
	speed = 6,
	turn = 5,
	radius = 0.45,
	cost = 250,
	-- Секунд на выпуск.
	time = 7,
	hp = 150,
	sight = 11,
	-- Турели на корпусе: вид из turrets/ и место — вперёд и вправо от центра, в тайлах.
	mounts = { { turret = "gunner", along = -0.16, across = 0 } },
}

-- Из чего сделана сущность: функция от вида, чтобы правки данных другими модами доходили до юнитов.
buggy.entity = function(kind)
	local d = kind.data
	return {
		Position = {},
		Facing = {},
		Owner = {},
		Drive = { move = "wheels", speed = d.speed, turn = d.turn },
		Unit = { radius = d.radius },
	}
end

-- Облик — чертёж из lib/units (перенос unitArt.ts), звук — мотор.
buggy.look = units.look("buggy", buggy.data.mounts)
buggy.look.sound = {
	voice = "motor",
	gain = 0.5,
	control = function(knobs, unit)
		knobs.pitch = 0.9 + (unit.entity % 7) * 0.03
	end,
}
buggy.look.icon = { direction = 56 }

return buggy
