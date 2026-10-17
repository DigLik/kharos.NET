--!strict
-- Камера слежения: держит в центре сущность из c.shared.follow (или первую выделенную) и мягко догоняет её; колесо —
-- масштаб. Так смотрит игра от персонажа — партия с этой камерой и управлением WASD будет совсем другой игрой.

local follow = {}

follow.data = {
	zooms = { 16, 24, 32, 48, 64 },
	start = 3,
	-- Насколько быстро догоняет: доля расстояния за секунду.
	catch = 6,
}

local level = follow.data.start

function follow.start(cam, c)
	cam.zoom = follow.data.zooms[level]
end

function follow.update(cam, c)
	local target = c.shared.follow or (c.shared.selection or {})[1]
	if target and c:alive(target) then
		local at = c:get(target, "Position")
		local k = math.min(1, follow.data.catch * c.dt)
		cam.x += (at.x - cam.x) * k
		cam.y += (at.y - cam.y) * k
	end
	local input = c.input
	if input.wheel ~= 0 and not input.over_ui then
		level = math.clamp(level + (if input.wheel > 0 then 1 else -1), 1, #follow.data.zooms)
		cam.zoom = follow.data.zooms[level]
	end
end

return follow
