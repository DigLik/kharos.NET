--!strict
-- Погода мира — перенос packages/sim-ts/src/weather.ts: смена дня и ночи и медленно бродящие ветер и осадки. Состояния
-- у неё нет — это функция параметров мира и времени игры, поэтому все игроки видят одну и ту же погоду. На игру не
-- влияет: свет сцены, осадки по биомам, ветер — и его голос.

local weather = {}

weather.data = {
	-- Сколько секунд игры длятся сутки; 0 — время стоит.
	dayLength = 1200,
	-- Который час в начале мира, от 0 до 24.
	startHour = 9,
	-- Меняются ли ветер и осадки; false — всегда ясно и лёгкий ветер.
	changes = true,
}

-- Освещение глубокой ночью: в темноте видны фары, огни зданий и вспышки боя.
local NIGHT_LIGHT = 0.1
-- За сколько секунд игры ветер и осадки заметно меняются: погода бродит медленно.
local WIND_PERIOD = 300
local RAIN_PERIOD = 900
-- Сильнейший ветер, тайлов в секунду, и ветер, когда погода не меняется.
local WIND_MAX = 3
local CALM_WIND = { x = 1.5, y = 0.4 }
-- Сколько времени идут осадки: шум выше этого порога — дождь или снег, ниже — ясно.
local RAIN_THRESHOLD = 0.55

weather.voices = { air = "wind" }

local function smooth(t: number): number
	return t * t * (3 - 2 * t)
end

-- Гладкий шум Перлина по одной оси: от -1 до 1, одно и то же значение для одних t и канала.
local function perlin(c, t: number, seed: number, channel: number): number
	local i = math.floor(t)
	local f = t - i
	local function gradient(cell: number): number
		return c.hash(cell, channel, seed) * 2 - 1
	end
	local a = gradient(i) * f
	local b = gradient(i + 1) * (f - 1)
	return math.clamp((a + (b - a) * smooth(f)) * 2, -1, 1)
end

-- Два слоя шума: крупные перемены и мелкая рябь поверх.
local function drift(c, t: number, seed: number, channel: number): number
	return perlin(c, t, seed, channel) * 0.75 + perlin(c, t * 3.1, seed, channel + 7) * 0.25
end

-- Сид мира — у местности партии.
local function seedOf(c): number
	local land = c.game.data and c.game.data.land
	local kind = land and c.protos.lands[land]
	return if kind then kind.data.seed else 0
end

function weather.update(a, c, knobs)
	local d = weather.data
	local seconds = c.clock
	local hour = ((d.startHour + (if d.dayLength > 0 then seconds / d.dayLength * 24 else 0)) % 24 + 24) % 24
	-- Солнце: выше нуля — день; сумерки мягкие, полдень — ярче всего.
	local sun = math.sin((hour - 6) / 12 * math.pi)
	local day = smooth(math.clamp((sun + 0.2) / 0.6, 0, 1))
	local light = NIGHT_LIGHT + (1 - NIGHT_LIGHT) * day
	local wx, wy, precipitation = CALM_WIND.x, CALM_WIND.y, 0
	if d.changes then
		local seed = seedOf(c)
		local rain = (drift(c, seconds / RAIN_PERIOD, seed, 3) + 1) / 2
		precipitation = math.clamp((rain - RAIN_THRESHOLD) / (1 - RAIN_THRESHOLD) * 1.5, 0, 1)
		-- В непогоду ветер крепче.
		local strength = WIND_MAX * (0.5 + 0.5 * precipitation)
		light *= 1 - 0.35 * precipitation
		wx = drift(c, seconds / WIND_PERIOD, seed, 1) * strength
		wy = drift(c, seconds / WIND_PERIOD, seed, 2) * strength * 0.6
	end
	a.hour = hour
	a.light = light
	a.wind = { x = wx, y = wy }
	a.precipitation = precipitation
	c.music.mood = if light < 0.5 then "night" else "calm"
	-- Голос ветра: сильнее ветер — громче и выше.
	local share = math.clamp(math.sqrt(wx * wx + wy * wy) / WIND_MAX, 0, 1)
	knobs.air.strength = share
	knobs.air.gain = 0.1 + share * 0.5 + precipitation * 0.2
end

return weather
