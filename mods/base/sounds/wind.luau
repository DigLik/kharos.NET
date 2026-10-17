--!strict
-- Ветер: розовый шум через полосовой фильтр, который гуляет сам и по силе ветра. Ручка strength — 0..1.

return {
	graph = function(s)
		local air = s:noise("pink")
		local band = s:filter("bandpass", 500, 0.8)
		s:lfo(0.17, 220):to(band.frequency)
		local level = s:gain(0.4)
		air:to(band):to(s:filter("lowpass", 1600, 0.5)):to(level):to(s:out())
		return { band = band, level = level }
	end,
	update = function(n, knobs)
		local strength = knobs.strength or 0
		n.band.frequency = 280 + strength * 900
		n.level.gain = 0.35 + strength * 0.65
	end,
}
