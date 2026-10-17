--!strict
-- Багги: данные, облик (чертёж и звук) — одним файлом.

local function rotate(angle: number, ax: number, ay: number): (number, number)
	local c, s = math.cos(angle), math.sin(angle)
	return ax * c - ay * s, ax * s + ay * c
end

return {
	kind = "unit",
	data = { speed = 4, turn = 3, radius = 0.4, hp = 60, move = "wheels" },

	look = {
		frame = 32,
		directions = 64,
		-- Чертёж вида сверху на холсте в пикселях спрайта, начало — центр юнита. Цвета с тоном — маска цвета стороны.
		draw = function(canvas, angle: number, phase: number)
			-- Колёса.
			for _, wheel in { { 6, 5 }, { 6, -5 }, { -6, 5 }, { -6, -5 } } do
				local wx, wy = rotate(angle, wheel[1], wheel[2])
				local ex, ey = rotate(angle, wheel[1] + 2.5, wheel[2])
				canvas:line(wx - (ex - wx), wy - (ey - wy), ex, ey, 2.5, 0x202428)
			end
			-- Корпус и рама.
			local fx, fy = rotate(angle, 8, 0)
			local bx, by = rotate(angle, -8, 0)
			canvas:line(bx, by, fx, fy, 7, canvas:team(1))
			canvas:line(bx, by, fx, fy, 4, canvas:team(2))
			-- Кабина и фара.
			local cx, cy = rotate(angle, -1, 0)
			canvas:circle(cx, cy, 2.5, 0x2a3138)
			local lx, ly = rotate(angle, 8.5, 0)
			canvas:circle(lx, ly, 1, if phase == 0 then 0xfff2b0 else 0xffd060)
		end,
		-- Звук — синтез: голос мотора и его параметры, а не запись.
		sound = { voice = "motor", pitch = 0.8, rough = 0.3 },
	},
}
