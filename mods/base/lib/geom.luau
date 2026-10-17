--!strict
-- Геометрия, общая для правил и облика.

local geom = {}

geom.TURN = math.pi * 2

-- Угол в (−π, π].
function geom.wrap(angle: number): number
	return angle - geom.TURN * math.floor(angle / geom.TURN + 0.5)
end

-- Точка (x, y), повёрнутая на angle вокруг начала.
function geom.rotate(angle: number, x: number, y: number): (number, number)
	local c, s = math.cos(angle), math.sin(angle)
	return x * c - y * s, x * s + y * c
end

-- Места группы вокруг (x, y): спиралью через spacing — первый в центре.
function geom.spread(count: number, x: number, y: number, spacing: number): { { number } }
	local places = {}
	local ring, side, step = 0, 0, 0
	local px, py = 0, 0
	for i = 1, count do
		places[i] = { x + px * spacing, y + py * spacing }
		-- Квадратная спираль: вправо, вниз, влево, вверх, каждый виток длиннее.
		if step == 0 then
			ring += 1
			px, py = -ring, -ring
			side, step = 0, ring * 8
		end
		local edge = ring * 2
		if side < edge then
			px += 1
		elseif side < edge * 2 then
			py += 1
		elseif side < edge * 3 then
			px -= 1
		else
			py -= 1
		end
		side += 1
		step -= 1
	end
	return places
end

return geom
