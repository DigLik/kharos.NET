--!strict
-- Облик завода: корпус, ворота цеха, труба и мигающие огни.

return {
	frame = 64,
	directions = 1,
	phases = 4,
	draw = function(canvas, _angle: number, phase: number)
		-- Основание и корпус (3 × 3 тайла — 48 пикселей, кадр шире ради трубы и тени).
		canvas:rect(0, 0, 48, 48, 0, 0x3a3f46)
		canvas:rect(0, -2, 44, 40, 0, canvas:team(0))
		canvas:rect(0, -3, 40, 34, 0, canvas:team(1))
		-- Пилообразная крыша: три ската.
		for i = -1, 1 do
			canvas:rect(i * 13, -8, 11, 16, 0, canvas:team(2))
			canvas:rect(i * 13 + 4, -8, 3, 16, 0, 0x9fb4c8)
		end
		-- Ворота цеха внизу.
		canvas:rect(0, 18, 18, 10, 0, 0x1c2026)
		for i = 0, 3 do
			canvas:rect(0, 14 + i * 2.5, 18, 1, 0, 0x2b3038)
		end
		-- Труба.
		canvas:circle(15, -17, 4, 0x2a2f36)
		canvas:circle(15, -17, 2.5, 0x14171b)
		-- Огни у ворот: бегут по кругу фазами.
		for i = 0, 3 do
			local on = (i == phase)
			canvas:circle(-11 + i * 7.3, 24, 1.2, if on then 0xffd060 else 0x6b5a2a)
		end
	end,
	lights = {
		glows = { { 0, 24, 6 } },
	},
	-- Цех гудит, громче — когда работает.
	sound = {
		voice = "hum",
		gain = 0.6,
		control = function(knobs, unit, c)
			local producer = c:get(unit.entity, "Producer")
			knobs.load = if producer and producer.unit ~= "" then 1 else 0.15
		end,
	},
}
