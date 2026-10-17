--!strict
-- Гул цеха: два синуса на 55 и 110 Гц, медленное дрожание громкости. Ручка load — работает ли цех.

return {
	graph = function(s)
		local low = s:osc("sine", 55)
		local high = s:osc("triangle", 110)
		local mix = s:gain(1)
		low:to(mix)
		high:to(s:gain(0.25)):to(mix)
		local tremolo = s:gain(0.8)
		s:lfo(0.7, 0.2):to(tremolo.gain)
		local level = s:gain(0.05)
		mix:to(tremolo):to(s:filter("lowpass", 500, 0.7)):to(level):to(s:out())
		return { level = level }
	end,
	update = function(n, knobs)
		n.level.gain = 0.03 + (knobs.load or 0) * 0.22
	end,
}
