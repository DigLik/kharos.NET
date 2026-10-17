--!strict
-- Сутки: свет сцены по времени игры, огни юнитов в сумерках и ночью, ночью музыка мрачнее.

local day = {}

day.data = {
	-- Секунд игры на сутки.
	length = 360,
	-- С какой доли суток начинается партия: 0.3 — утро.
	start = 0.3,
	-- Цвет света по времени суток: доля суток, r, g, b, сила огней.
	keys = {
		{ 0.00, 0.16, 0.20, 0.42, 1 },
		{ 0.20, 0.18, 0.22, 0.45, 1 },
		{ 0.27, 0.85, 0.62, 0.62, 0.4 },
		{ 0.33, 1.00, 1.00, 1.00, 0 },
		{ 0.70, 1.00, 1.00, 1.00, 0 },
		{ 0.77, 1.00, 0.72, 0.52, 0.3 },
		{ 0.83, 0.30, 0.28, 0.52, 1 },
		{ 1.00, 0.16, 0.20, 0.42, 1 },
	},
}

local function sample(t: number): (number, number, number, number)
	local keys = day.data.keys
	for i = 1, #keys - 1 do
		local a, b = keys[i], keys[i + 1]
		if t >= a[1] and t <= b[1] then
			local k = (t - a[1]) / (b[1] - a[1])
			local function mix(index: number): number
				return a[index] + (b[index] - a[index]) * k
			end
			return mix(2), mix(3), mix(4), mix(5)
		end
	end
	local last = keys[#keys]
	return last[2], last[3], last[4], last[5]
end

function day.update(a, c)
	local t = (c.tick * 0.05 / day.data.length + day.data.start) % 1
	local r, g, b, lamps = sample(t)
	a.light = { r = r, g = g, b = b }
	a.lamps = lamps
	a.clock = t
	c.music.mood = if lamps > 0.5 then "night" else "calm"
end

return day
