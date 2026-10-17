--!strict
--!native
-- Ходовая «колёса»: поворот с ограниченной скоростью и ход туда, куда смотрит; чем сильнее цель в стороне, тем
-- медленнее, а цель за спиной — разворот на месте. Система работает над столбцами всех юнитов на колёсах разом.

local geom = require("lib/geom")

local readf64, writef64 = buffer.readf64, buffer.writef64
local wrap = geom.wrap

local wheels = {
	data = { grip = 1 },

	-- Drive — ходовая юнита: какая (move), скорость в тайлах в секунду, поворот в радианах в секунду, цель и едет ли.
	components = {
		Drive = { move = "", speed = 0, turn = 0, tx = 0, ty = 0, moving = false },
	},

	systems = {
		drive = {
			query = { "Position", "Facing", "Drive" },
			where = { Drive = { move = "wheels" } },
			write = { "Position", "Facing", "Drive" },
			run = function(q, ctx)
				local x, y, facing = q.Position.x, q.Position.y, q.Facing.angle
				local d = q.Drive
				local dt = ctx.dt
				for i = 0, q.n - 1 do
					local at = i * 8
					if readf64(d.moving, at) == 0 then
						continue
					end
					local px, py = readf64(x, at), readf64(y, at)
					local dx, dy = readf64(d.tx, at) - px, readf64(d.ty, at) - py
					local distance = math.sqrt(dx * dx + dy * dy)
					local step = readf64(d.speed, at) * dt
					local face = readf64(facing, at)
					if distance <= step then
						writef64(x, at, px + dx)
						writef64(y, at, py + dy)
						writef64(d.moving, at, 0)
						continue
					end
					local turn = readf64(d.turn, at) * dt
					local off = wrap(math.atan2(dy, dx) - face)
					if math.abs(off) <= turn then
						face += off
					else
						face += if off > 0 then turn else -turn
					end
					writef64(facing, at, wrap(face))
					local ahead = math.cos(off)
					if ahead > 0 then
						writef64(x, at, px + math.cos(face) * step * ahead)
						writef64(y, at, py + math.sin(face) * step * ahead)
					end
				end
			end,
		},
	},
}

return wheels
