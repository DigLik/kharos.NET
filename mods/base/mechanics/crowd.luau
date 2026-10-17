--!strict
-- Теснота: юниты не наезжают друг на друга — после хода их расталкивает пакетный инструмент движка ctx:separate.

return {
	data = {
		-- Доля перекрытия корпусов, снимаемая за тик.
		strength = 0.5,
	},
	systems = {
		separate = {
			query = { "Position", "Unit", "Drive" },
			write = { "Position" },
			after = "drive",
			run = function(q, ctx, kind)
				ctx:separate(q, kind.data.strength)
			end,
		},
	},
}
