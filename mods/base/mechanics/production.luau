--!strict
-- Производство: здание с Producer выпускает юнитов за кредиты. Заказ платится сразу, очередь — по одному, готовый
-- юнит выходит у ворот и едет к точке сбора. Приказы: produce, cancel, rally.

local wallet = require("lib/wallet")

local readf64, writef64 = buffer.readf64, buffer.writef64

local production = {}

production.data = {
	-- Сколько заказов держит очередь.
	queue = 5,
}

production.components = {
	-- unit — вид, который делается (пусто — ничего), progress — сколько секунд уже идёт, queue — что заказано следом,
	-- точка сбора — rallyX, rallyY, если rally.
	Producer = { unit = "", progress = 0, queue = {}, rally = false, rallyX = 0, rallyY = 0 },
}

-- Своё здание с Producer, которое умеет делать unit; иначе nil.
local function producer(ctx, player: number, building: number, unit: string?)
	if type(building) ~= "number" or not ctx:alive(building) then
		return nil
	end
	local owner = ctx:get(building, "Owner")
	local state = ctx:get(building, "Producer")
	if not owner or owner.player ~= player or not state then
		return nil
	end
	if unit then
		local kind = ctx:proto(ctx:get(building, "Proto").id)
		if not table.find(kind.data.produces or {}, unit) or not ctx.protos.units[unit] then
			return nil
		end
	end
	return state
end

production.commands = {
	-- { building, unit } — заказать юнит; кредиты списываются сразу.
	produce = function(ctx, player, args)
		local state = producer(ctx, player, args.building, args.unit)
		if not state then
			return
		end
		local queue = state.queue
		local busy = if state.unit ~= "" then 1 else 0
		if #queue + busy >= production.data.queue then
			return
		end
		local unit = ctx.protos.units[args.unit]
		if not wallet.pay(ctx, player, unit.data.cost) then
			ctx:signal("refused", { player = player, reason = "credits" })
			return
		end
		if state.unit == "" then
			ctx:set(args.building, "Producer", { unit = args.unit, progress = 0 })
		else
			table.insert(queue, args.unit)
			ctx:set(args.building, "Producer", { queue = queue })
		end
		ctx:signal("ordered", { player = player, building = args.building, unit = args.unit })
	end,

	-- { building } — снять последний заказ с возвратом кредитов.
	cancel = function(ctx, player, args)
		local state = producer(ctx, player, args.building)
		if not state then
			return
		end
		local queue = state.queue
		local unit = table.remove(queue)
		if unit then
			ctx:set(args.building, "Producer", { queue = queue })
		elseif state.unit ~= "" then
			unit = state.unit
			ctx:set(args.building, "Producer", { unit = "", progress = 0 })
		end
		if unit then
			wallet.refund(ctx, player, ctx.protos.units[unit].data.cost)
		end
	end,

	-- { building, x, y } — точка сбора.
	rally = function(ctx, player, args)
		if producer(ctx, player, args.building) and type(args.x) == "number" and type(args.y) == "number" then
			ctx:set(args.building, "Producer", { rally = true, rallyX = args.x, rallyY = args.y })
		end
	end,
}

production.systems = {
	produce = {
		query = { "Producer", "Position", "Owner" },
		write = { "Producer" },
		run = function(q, ctx)
			local p = q.Producer
			local entity = q.entity
			local empty = ctx:id("")
			for i = 0, q.n - 1 do
				local at = i * 8
				local current = readf64(p.unit, at)
				if current == empty then
					continue
				end
				local unit = ctx.protos.units[ctx:text(current)]
				local progress = readf64(p.progress, at) + ctx.dt
				if progress < unit.data.time then
					writef64(p.progress, at, progress)
					continue
				end
				-- Готово: юнит у ворот, едет к точке сбора или чуть вперёд.
				local building = readf64(entity, at)
				local kind = ctx:proto(ctx:get(building, "Proto").id)
				local x, y = readf64(q.Position.x, at), readf64(q.Position.y, at)
				local exit = kind.data.exit or { 0, 1 }
				local ox, oy = x + exit[1], y + exit[2]
				local tx, ty = ox, oy + 1.5
				if readf64(p.rally, at) ~= 0 then
					tx, ty = readf64(p.rallyX, at), readf64(p.rallyY, at)
				end
				local player = readf64(q.Owner.player, at)
				local made = ctx:spawn(unit, {
					Position = { x = ox, y = oy },
					Facing = { angle = math.atan2(ty - oy, tx - ox) },
					Owner = { player = player },
					Drive = { tx = tx, ty = ty, moving = true },
				})
				ctx:signal("produced", { player = player, building = building, unit = unit.name, entity = made, x = ox, y = oy })
				-- Следующий из очереди.
				local queue = ctx:get(building, "Producer").queue
				local next = table.remove(queue, 1)
				writef64(p.progress, at, 0)
				writef64(p.unit, at, if next then ctx:id(next) else empty)
				ctx:set(building, "Producer", { queue = queue })
			end
		end,
	},
}

return production
