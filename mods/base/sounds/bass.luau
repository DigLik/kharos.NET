--!strict
-- Бас: синус с квадратом через фильтр.

return {
	graph = function(s)
		local mix = s:gain(1)
		s:osc("sine", s.note):to(mix)
		s:osc("square", s.note):to(s:gain(0.12)):to(mix)
		local env = s:env({ attack = 0.02, decay = 0.3, sustain = 0.6, release = 0.4, peak = 0.3 })
		mix:to(s:filter("lowpass", 380, 1.2)):to(env):to(s:out())
	end,
}
