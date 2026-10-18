--!strict
-- Турели на носителях: новой сущности, у вида которой есть data.mounts, ставятся турели — свои сущности с Attached.
-- Каждый тик после хода турель встаёт на своё место на носителе и смотрит туда же, куда он, плюс свой угол
-- (Turret.angle); турель погибшего носителя исчезает. Как followCarriers в sim-ts.

local mounts = {}

mounts.components = {
	-- Носитель и место на нём: вперёд и вправо от центра, в тайлах.
	Attached = { parent = 0, along = 0, across = 0 },
	-- Угол турели относительно носителя.
	Turret = { angle = 0 },
}

mounts.on = {
	spawned = function(ctx, event)
		if not event.proto then
			return
		end
		local kind = ctx:proto(event.proto)
		local list = kind.data and kind.data.mounts
		if not list then
			return
		end
		local at = ctx:get(event.entity, "Position")
		local facing = ctx:get(event.entity, "Facing")
		local owner = ctx:get(event.entity, "Owner")
		for _, mount in list do
			ctx:spawn("turrets/" .. mount.turret, {
				Position = { x = at.x, y = at.y },
				Facing = { angle = if facing then facing.angle else 0 },
				Owner = { player = if owner then owner.player else 0 },
				Attached = { parent = event.entity, along = mount.along, across = mount.across },
			})
		end
	end,
}

mounts.systems = {
	follow = {
		query = { "Attached", "Position", "Facing", "Turret" },
		write = { "Position", "Facing" },
		after = { "drive", "separate" },
		run = function(q, ctx)
			local a, x, y, facing, turret = q.Attached, q.Position.x, q.Position.y, q.Facing.angle, q.Turret.angle
			for i = 0, q.n - 1 do
				local at = i * 8
				local parent = buffer.readf64(a.parent, at)
				local place = ctx:get(parent, "Position")
				if not place then
					ctx:destroy(buffer.readf64(q.entity, at))
					continue
				end
				local carrier = ctx:get(parent, "Facing")
				local angle = if carrier then carrier.angle else 0
				local along, across = buffer.readf64(a.along, at), buffer.readf64(a.across, at)
				local c, s = math.cos(angle), math.sin(angle)
				buffer.writef64(x, at, place.x + along * c - across * s)
				buffer.writef64(y, at, place.y + along * s + across * c)
				buffer.writef64(facing, at, angle + buffer.readf64(turret, at))
			end
		end,
	},
}

return mounts
