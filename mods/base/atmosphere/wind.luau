--!strict
-- Ветер: медленно меняет силу и направление, несёт пыль по экрану и шумит.

local wind = {}

wind.data = {
	-- Сила ветра в тайлах в секунду: от и до.
	calm = 0.4,
	gust = 3.2,
	dust = 0xd8c39a,
	-- Пылинок в секунду на экран при самом сильном ветре.
	rate = 90,
}

wind.voices = { air = "wind" }

-- Плавный шум от времени: сумма синусов с разными периодами.
local function wave(t: number, seed: number): number
	return (math.sin(t * 0.13 + seed) + math.sin(t * 0.071 + seed * 2.3) * 0.6 + math.sin(t * 0.31 + seed * 0.7) * 0.25) / 1.85
end

local spawn = 0

function wind.update(a, c, knobs)
	local t = c.tick * 0.05 + c.dt
	local strength = wind.data.calm + (wind.data.gust - wind.data.calm) * (wave(t, 1.7) * 0.5 + 0.5)
	local angle = 0.6 + wave(t * 0.5, 4.1) * 0.9
	local wx, wy = math.cos(angle) * strength, math.sin(angle) * strength * 0.6
	a.wind = { x = wx, y = wy }
	-- Пыль: летит с ветром по всему экрану.
	local share = (strength - wind.data.calm) / (wind.data.gust - wind.data.calm)
	spawn += wind.data.rate * share * share * c.dt
	local x1, y1 = c:to_world(0, 0)
	local x2, y2 = c:to_world(c.screen.w, c.screen.h)
	while spawn >= 1 do
		spawn -= 1
		local px = x1 + math.random() * (x2 - x1)
		local py = y1 + math.random() * (y2 - y1)
		c:particle({ x = px, y = py, vx = 0, vy = 0, life = 1.5 + math.random() * 2, color = wind.data.dust + 0x30000000 + math.floor(math.random() * 0x40) * 0x1000000, size = 1, drift = true })
	end
	knobs.air.strength = share
	knobs.air.gain = 0.12 + share * 0.55
end

return wind
