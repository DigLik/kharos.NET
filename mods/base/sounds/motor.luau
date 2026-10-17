--!strict
-- Мотор лёгкой машины: пила и квадрат на октаву ниже, рокот бурого шума, всё через фильтр. Ручки: load — нагрузка
-- 0..1 (ход), pitch — множитель частоты.

return {
	graph = function(s)
		local base = s:osc("sawtooth", 42)
		local sub = s:osc("square", 21)
		local rumble = s:noise("brown")
		local filter = s:filter("lowpass", 320, 4)
		base:to(filter)
		sub:to(s:gain(0.35)):to(filter)
		rumble:to(s:gain(0.3)):to(filter)
		-- Дрожь оборотов.
		s:lfo(9, 2.5):to(base.frequency)
		local level = s:gain(0.2)
		filter:to(level):to(s:out())
		return { base = base, sub = sub, filter = filter, level = level }
	end,
	update = function(n, knobs)
		local load = knobs.load or 0
		local pitch = knobs.pitch or 1
		n.base.frequency = (38 + load * 46) * pitch
		n.sub.frequency = (19 + load * 23) * pitch
		n.filter.frequency = 260 + load * 1100
		n.level.gain = 0.12 + load * 0.4
	end,
}
