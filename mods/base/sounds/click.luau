--!strict
-- Щелчок интерфейса: запечённый — короткий синус с хлопком шума.

return {
	note = 440,
	bake = {
		length = 0.06,
		render = function(out, rate: number, n: number)
			for i = 0, n - 1 do
				local t = i / rate
				local tone = math.sin(2 * math.pi * 1900 * t) * math.exp(-t * 90) * 0.45
				local snap = (math.random() * 2 - 1) * math.exp(-t * 320) * 0.25
				buffer.writef32(out, i * 4, tone + snap)
			end
		end,
	},
}
