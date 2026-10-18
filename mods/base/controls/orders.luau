--!strict
-- Приказы правой кнопкой: выделенные юниты едут в точку, выделенный завод ставит туда точку сбора. Касанием — долгое
-- нажатие на земле. Отметка приказа гаснет за полсекунды.

local orders = {}

orders.data = {
	mark = 0x73eb8c,
	rally = 0x73eb8c,
	-- Долгое касание, секунд.
	hold = 0.5,
}

local marks: { { x: number, y: number, time: number, color: number } } = {}
local held: { time: number, x: number, y: number }? = nil

local function order(c, x: number, y: number)
	local units, building = {}, nil
	for _, e in c.shared.selection or {} do
		if c:has(e, "Drive") then
			table.insert(units, e)
		elseif c:has(e, "Producer") then
			building = e
		end
	end
	if #units > 0 then
		c:send("move", { units = units, x = x, y = y })
		table.insert(marks, { x = x, y = y, time = c.time, color = orders.data.mark })
	elseif building then
		c:send("rally", { building = building, x = x, y = y })
		table.insert(marks, { x = x, y = y, time = c.time, color = orders.data.rally })
		c:sound("order", { pitch = 0.75, gain = 0.5 })
	end
end

function orders.update(c)
	local input = c.input
	if input.pressed.right and input.inside and not input.over_ui then
		order(c, input.wx, input.wy)
	end
	-- Касание: долгое нажатие на месте — приказ.
	if input.pressed.left and input.touch and not input.over_ui then
		held = { time = c.time, x = input.x, y = input.y }
	end
	if held then
		local moved = math.abs(input.x - held.x) + math.abs(input.y - held.y) > 8
		if not input.down.left or moved then
			held = nil
		elseif c.time - held.time > orders.data.hold then
			order(c, input.wx, input.wy)
			c.shared.swallow = true
			held = nil
		end
	end
	if input.hits.x then
		c:send("stop", { units = c.shared.selection or {} })
	end
	-- Отметки: сходящееся кольцо.
	local alive = {}
	for _, mark in marks do
		local age = (c.time - mark.time) / 0.5
		if age < 1 then
			c.draw:circle(mark.x, mark.y, 0.8 * (1 - age) + 0.15, mark.color, 1.5, 1 - age)
			table.insert(alive, mark)
		end
	end
	marks = alive
end

return orders
