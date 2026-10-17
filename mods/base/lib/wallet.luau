--!strict
-- Кошельки игроков: сущность игрока с компонентом Player (mechanics/players.luau) по номеру — в ctx.storage.players.

local wallet = {}

-- Сущность игрока по его номеру; нет — nil.
function wallet.of(ctx, player: number): number?
	local list = ctx.storage.players
	return list and list[tostring(player)]
end

function wallet.credits(ctx, player: number): number
	local entity = wallet.of(ctx, player)
	return if entity then ctx:get(entity, "Player").credits else 0
end

-- Списывает amount кредитов, если хватает: true — списано.
function wallet.pay(ctx, player: number, amount: number): boolean
	local entity = wallet.of(ctx, player)
	if not entity then
		return false
	end
	local state = ctx:get(entity, "Player")
	if state.credits < amount then
		return false
	end
	ctx:set(entity, "Player", { credits = state.credits - amount })
	return true
end

function wallet.refund(ctx, player: number, amount: number)
	local entity = wallet.of(ctx, player)
	if entity then
		ctx:set(entity, "Player", { credits = ctx:get(entity, "Player").credits + amount })
	end
end

return wallet
