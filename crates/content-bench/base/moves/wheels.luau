--!strict
--!native
-- Ходовая «колёса»: поворот с ограниченной скоростью, ход туда, куда смотрит; чем сильнее смотрит мимо цели, тем
-- медленнее. Система работает над столбцами всех юнитов с этой ходовой разом — движок отдаёт их буферами f64.

local TURN = math.pi * 2

local function wrap(angle: number): number
	return angle - TURN * math.floor(angle / TURN + 0.5)
end

-- Доехавшему — новая цель: псевдослучайная точка поля, чтобы юниты ездили всё время.
local function retarget(i: number, tick: number): (number, number)
	local seed = (i * 7919 + tick * 104729) % 1000003
	return (seed % 500) + 0.5, ((seed // 500) % 500) + 0.5
end

return {
	kind = "move",
	data = { layer = "ground", grip = 1 },

	-- Столбцами: n юнитов, x, y, facing, tx, ty — буферы f64; speed, turn — числа вида (у всех юнитов замера один вид).
	systems = {
		move = function(c)
			local x, y, facing, tx, ty = c.x, c.y, c.facing, c.tx, c.ty
			local step, turn = c.speed * c.dt, c.turn * c.dt
			for i = 0, c.n - 1 do
				local at = i * 8
				local px, py = buffer.readf64(x, at), buffer.readf64(y, at)
				local gx, gy = buffer.readf64(tx, at), buffer.readf64(ty, at)
				local dx, dy = gx - px, gy - py
				if dx * dx + dy * dy < 0.25 then
					gx, gy = retarget(i, c.tick)
					buffer.writef64(tx, at, gx)
					buffer.writef64(ty, at, gy)
					dx, dy = gx - px, gy - py
				end
				local face = buffer.readf64(facing, at)
				local off = wrap(math.atan2(dy, dx) - face)
				if math.abs(off) <= turn then
					face += off
				else
					face += if off > 0 then turn else -turn
				end
				local ahead = math.cos(off)
				if ahead > 0 then
					buffer.writef64(x, at, px + math.cos(face) * step * ahead)
					buffer.writef64(y, at, py + math.sin(face) * step * ahead)
				end
				buffer.writef64(facing, at, face)
			end
		end,
	},

	-- То же на одного юнита: для сравнения с вызовом на сущность.
	step = function(px: number, py: number, face: number, gx: number, gy: number, step: number, turn: number): (number, number, number)
		local dx, dy = gx - px, gy - py
		local off = wrap(math.atan2(dy, dx) - face)
		if math.abs(off) <= turn then
			face += off
		else
			face += if off > 0 then turn else -turn
		end
		local ahead = math.cos(off)
		if ahead > 0 then
			px += math.cos(face) * step * ahead
			py += math.sin(face) * step * ahead
		end
		return px, py, face
	end,
}
