--!strict
-- Подложка музыки: две расстроенные пилы и треугольник октавой ниже, фильтр раскрывается к середине ноты.

return {
	graph = function(s)
		local mix = s:gain(0.3)
		s:osc("sawtooth", s.note):to(mix)
		s:osc("sawtooth", s.note * 1.004):to(mix)
		s:osc("triangle", s.note / 2):to(mix)
		local length = s.length or 4
		local filter = s:filter("lowpass", 400, 0.8)
		filter.frequency:set(380, s.at)
		filter.frequency:ramp(1300, s.at + length * 0.5)
		filter.frequency:ramp(450, s.at + length)
		local env = s:env({ attack = 1.2, decay = 0.6, sustain = 0.8, release = 2.2, peak = 0.16 })
		mix:to(filter):to(env):to(s:out())
	end,
}
