--!strict
-- Щипок: треугольник и синус октавой выше, быстрый спад, фильтр закрывается.

return {
	graph = function(s)
		local mix = s:gain(1)
		s:osc("triangle", s.note):to(mix)
		s:osc("sine", s.note * 2):to(s:gain(0.3)):to(mix)
		local filter = s:filter("lowpass", 3000, 1)
		filter.frequency:set(3200, s.at)
		filter.frequency:exp(500, s.at + 0.4)
		local env = s:env({ attack = 0.004, decay = 0.45, sustain = 0, release = 0.3, peak = 0.3 })
		mix:to(filter):to(env):to(s:out())
		-- Эхо — два отвода без петли: петля из узлов держала бы их живыми после ноты.
		local tone = s:filter("lowpass", 1800, 0.7)
		env:to(s:gain(0.3)):to(s:delay(0.36)):to(tone)
		env:to(s:gain(0.12)):to(s:delay(0.72)):to(tone)
		tone:to(s:out())
	end,
}
