--!strict
-- Юнит готов: колокольчик — основной тон и неровный обертон.

return {
	length = 0.4,
	note = 1046.5,
	graph = function(s)
		local env = s:env({ attack = 0.004, decay = 0.35, sustain = 0.15, release = 0.5, peak = 0.22 })
		s:osc("sine", s.note):to(env)
		s:osc("sine", s.note * 2.76):to(s:gain(0.25)):to(env)
		s:osc("sine", s.note * 0.5):to(s:gain(0.4)):to(env)
		env:to(s:out())
	end,
}
