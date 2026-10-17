--!strict
-- Приказы юнитам: ехать в точку. Группа встаёт вокруг точки спиралью, ближний к месту занимает ближнее.

local geom = require("lib/geom")

local orders = {}

orders.data = {
	-- Шаг мест группы, в тайлах.
	spacing = 1.2,
}

orders.commands = {
	-- { units = { сущности }, x, y } — свои юниты с ходовой едут к точке.
	move = function(ctx, player, args)
		if type(args.x) ~= "number" or type(args.y) ~= "number" or type(args.units) ~= "table" then
			return
		end
		local mine = {}
		for _, entity in args.units do
			if type(entity) == "number" and ctx:alive(entity) and ctx:has(entity, "Drive") then
				local owner = ctx:get(entity, "Owner")
				if owner and owner.player == player then
					table.insert(mine, entity)
				end
			end
		end
		if #mine == 0 then
			return
		end
		local places = geom.spread(#mine, args.x, args.y, orders.data.spacing)
		-- Жадно: каждому месту — ближайший из оставшихся юнитов.
		for _, place in places do
			local best, distance = 0, math.huge
			for index, entity in mine do
				local at = ctx:get(entity, "Position")
				local d = (at.x - place[1]) ^ 2 + (at.y - place[2]) ^ 2
				if d < distance then
					best, distance = index, d
				end
			end
			local entity = table.remove(mine, best)
			ctx:set(entity, "Drive", { tx = place[1], ty = place[2], moving = true })
		end
		ctx:signal("moved", { player = player, x = args.x, y = args.y })
	end,

	-- { units } — стоять.
	stop = function(ctx, player, args)
		for _, entity in args.units or {} do
			if type(entity) == "number" and ctx:alive(entity) and ctx:has(entity, "Drive") then
				local owner = ctx:get(entity, "Owner")
				if owner and owner.player == player then
					ctx:set(entity, "Drive", { moving = false })
				end
			end
		end
	end,
}

return orders
