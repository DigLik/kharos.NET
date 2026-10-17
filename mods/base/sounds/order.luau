--!strict
-- Подтверждение приказа: два коротких сигнала квинтой вверх.

return {
	length = 0.12,
	note = 880,
	graph = function(s)
		local osc = s:osc("square", s.note)
		osc.frequency:set(s.note * 1.5, s.at + 0.06)
		local env = s:env({ attack = 0.003, decay = 0.04, sustain = 0.6, release = 0.05, peak = 0.12 })
		osc:to(s:filter("lowpass", 2400, 1)):to(env):to(s:out())
	end,
}
