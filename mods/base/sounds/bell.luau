--!strict
-- Колокол ночной музыки: синус с обертонами 2,76 и 5,4 и долгим хвостом.

return {
	graph = function(s)
		local env = s:env({ attack = 0.01, decay = 1.6, sustain = 0, release = 1.2, peak = 0.18 })
		s:osc("sine", s.note):to(env)
		s:osc("sine", s.note * 2.76):to(s:gain(0.3)):to(env)
		s:osc("sine", s.note * 5.4):to(s:gain(0.08)):to(env)
		env:to(s:out())
		-- Эхо — два отвода без петли.
		env:to(s:gain(0.35)):to(s:delay(0.5)):to(s:out())
		env:to(s:gain(0.14)):to(s:delay(1.0)):to(s:out())
	end,
}
