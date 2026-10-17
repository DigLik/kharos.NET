--!strict
-- Отказ: низкое жужжание.

return {
	length = 0.16,
	note = 140,
	graph = function(s)
		local env = s:env({ attack = 0.005, decay = 0.05, sustain = 0.8, release = 0.06, peak = 0.12 })
		s:osc("square", s.note):to(s:filter("lowpass", 700, 2)):to(env):to(s:out())
	end,
}
