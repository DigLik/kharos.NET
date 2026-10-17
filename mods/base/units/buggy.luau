--!strict
-- Багги: быстрый лёгкий разведчик на колёсах. Данные, правила и облик — одним файлом.

local geom = require("lib/geom")
local rotate = geom.rotate

local buggy = {}

buggy.data = {
	title = "Багги",
	speed = 4,
	turn = 3,
	radius = 0.45,
	cost = 100,
	-- Секунд на выпуск.
	time = 4,
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

-- Облик — только клиенту: чертёж вида сверху на холсте в пикселях спрайта (16 на тайл), начало — центр юнита.
-- Цвета с тоном (canvas:team) — цвет стороны: его перекрашивает шейдер палитрой игрока.
buggy.look = {
	frame = 32,
	directions = 64,
	phases = 2,
	draw = function(canvas, angle: number, phase: number)
		-- Колёса: передние и задние, у движущегося протектор мигает фазой.
		for _, wheel in { { 6, 5 }, { 6, -5 }, { -6, 5 }, { -6, -5 } } do
			local wx, wy = rotate(angle, wheel[1], wheel[2])
			local ex, ey = rotate(angle, wheel[1] + 2.5, wheel[2])
			canvas:line(wx - (ex - wx), wy - (ey - wy), ex, ey, 2.5, if phase == 0 then 0x202428 else 0x2b3036)
		end
		-- Корпус и рама.
		local fx, fy = rotate(angle, 8, 0)
		local bx, by = rotate(angle, -8, 0)
		canvas:line(bx, by, fx, fy, 7, canvas:team(1))
		canvas:line(bx, by, fx, fy, 4, canvas:team(2))
		-- Дуга безопасности и кабина.
		local ax, ay = rotate(angle, -2, -3)
		local cx, cy = rotate(angle, -2, 3)
		canvas:line(ax, ay, cx, cy, 1.5, 0x3a4048)
		local hx, hy = rotate(angle, -1, 0)
		canvas:circle(hx, hy, 2.5, 0x2a3138)
		-- Фара.
		local lx, ly = rotate(angle, 8.5, 0)
		canvas:circle(lx, ly, 1, 0xfff2b0)
	end,
	-- Огни: фары — пятна (along, across — в пикселях спрайта от центра вперёд и вбок, glow — радиус) и луч.
	lights = {
		lamps = { { 7, -2, 2 }, { 7, 2, 2 } },
		beam = { along = 13, length = 60, near = 4, spread = 0.28, level = 1 },
	},
	-- Звук — синтез: голос из sounds/ и как его ручки зависят от хода юнита (load — доля полной скорости).
	sound = {
		voice = "motor",
		gain = 0.5,
		control = function(knobs, unit)
			knobs.pitch = 0.9 + (unit.entity % 7) * 0.03
		end,
	},
	icon = { direction = 56 },
}

return buggy
