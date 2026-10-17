--!strict
-- Панель игрока: кредиты и время суток сверху, внизу — что выделено, а у выделенного завода — производство.

local hud = {}

hud.data = {
	panel = 0x14161c,
	line = 0x3a4250,
	text = 0xdedee4,
	dim = 0x8a8a98,
	gold = 0xffd060,
	accent = 0x5aa9ff,
}

local colors = hud.data

-- Кредиты своего игрока.
local function credits(c): number
	for _, e in c:each({ "Player" }) do
		local player = c:get(e, "Player")
		if player.id == c.player then
			return player.credits
		end
	end
	return 0
end

local function frame(c, x: number, y: number, w: number, h: number)
	c.ui:panel(x, y, w, h, colors.panel, 0.94)
	c.ui:outline(x, y, w, h, 1, colors.line)
end

-- Производство завода e.
local function factory(c, e: number, kind, x: number, y: number)
	local producer = c:get(e, "Producer")
	local money = credits(c)
	local units = c.protos.units
	c.ui:text(x + 10, y + 8, kind.data.title or kind.name, 12, colors.text)
	-- Заказ: значок, имя, цена.
	local bx = x + 10
	for _, name in kind.data.produces or {} do
		local unit = units[name]
		if unit then
			local cost = unit.data.cost or 0
			local clicked, hovered = c.ui:button("produce/" .. name, bx, y + 28, 64, 64, nil, { enabled = money >= cost })
			c.ui:icon(unit, bx + 8, y + 30, 48)
			c.ui:text(bx + 4, y + 78, tostring(cost), 10, if money >= cost then colors.gold else colors.dim)
			if clicked then
				c:send("produce", { building = e, unit = name })
				c:sound("click", { pitch = 1.3, gain = 0.4 })
			end
			if hovered then
				c.ui:text(bx, y + 98, (unit.data.title or name) .. " — " .. cost .. " кр., " .. (unit.data.time or 0) .. " с", 10, colors.dim)
			end
			bx += 70
		end
	end
	-- Очередь: что делается — с полосой, следом — значки поменьше.
	local qx = x + 230
	if producer.unit ~= "" then
		local unit = units[producer.unit]
		c.ui:icon(unit, qx, y + 30, 40)
		c.ui:bar(qx, y + 74, 40, 4, producer.progress / (unit.data.time or 1), colors.accent)
		for i, name in producer.queue do
			c.ui:icon(units[name], qx + 18 + i * 30, y + 40, 26)
		end
		local clicked = c.ui:button("cancel", qx + 200, y + 28, 24, 24, "×", { size = 12 })
		if clicked then
			c:send("cancel", { building = e })
		end
	else
		c.ui:text(qx, y + 44, "Цех свободен", 10, colors.dim)
	end
	if producer.rally then
		c.ui:text(qx, y + 82, "Точка сбора задана", 10, colors.dim)
	else
		c.ui:text(qx, y + 82, "ПКМ по земле — точка сбора", 10, colors.dim)
	end
end

function hud.draw(c)
	local screen = c.screen
	-- Кредиты.
	local money = math.floor(credits(c))
	frame(c, 10, 10, 170, 30)
	c.ui:text(20, 18, "Кредиты", 12, colors.dim)
	c.ui:text(90, 18, tostring(money), 12, colors.gold)
	-- Время суток.
	local clock = c.atmosphere.clock
	if clock then
		local text = string.format("%02d:%02d", math.floor(clock * 24), math.floor(clock * 24 * 60) % 60)
		frame(c, screen.w - 80, 10, 70, 30)
		c.ui:text(screen.w - 62, 18, text, 12, colors.text)
	end
	-- Выделенное.
	local selection = c.shared.selection or {}
	if #selection == 0 then
		return
	end
	local w, h = 480, 120
	local x, y = math.floor((screen.w - w) / 2), screen.h - h - 10
	frame(c, x, y, w, h)
	if #selection == 1 and c:has(selection[1], "Producer") then
		factory(c, selection[1], c:proto(selection[1]), x, y)
		return
	end
	c.ui:text(x + 10, y + 8, "Выделено: " .. #selection, 12, colors.text)
	for i, e in selection do
		if i > 24 then
			break
		end
		local column, row = (i - 1) % 12, (i - 1) // 12
		local kind = c:proto(e)
		if kind then
			c.ui:icon(kind, x + 10 + column * 38, y + 30 + row * 40, 36)
		end
	end
end

return hud
