--!strict
-- Выделение: щелчок — своя сущность под курсором, рамка — свои юниты в ней, Shift — добавить, Esc — снять,
-- двойной щелчок — все свои того же вида на экране. Выделенное — c.shared.selection (его видят приказы и панели).

local select = {}

select.data = {
	-- Сдвиг указателя, после которого щелчок становится рамкой, в пикселях.
	threshold = 6,
	-- Двойной щелчок — за столько секунд.
	double = 0.35,
	ring = 0x7cff8a,
}

local start: { x: number, y: number, wx: number, wy: number }? = nil
local last = { time = -1, entity = 0 }

-- Радиус кольца выделения: у юнита — корпус, у здания — половина большей стороны.
local function radius(c, e: number): number
	local unit = c:get(e, "Unit")
	if unit then
		return unit.radius + 0.15
	end
	local building = c:get(e, "Building")
	return if building then math.max(building.width, building.height) * 0.62 else 0.6
end

function select.update(c)
	local input = c.input
	local shared = c.shared
	local selection = shared.selection or {}
	-- Погибшие выпадают сами.
	local alive = {}
	for _, e in selection do
		if c:alive(e) then
			table.insert(alive, e)
		end
	end
	selection = alive

	if input.pressed.left and input.inside and not input.over_ui then
		start = { x = input.x, y = input.y, wx = input.wx, wy = input.wy }
	end
	if start then
		local moved = math.abs(input.x - start.x) + math.abs(input.y - start.y) > select.data.threshold
		if input.down.left and moved then
			c.draw:rect(start.wx, start.wy, input.wx, input.wy, 0x7cff8a, nil, 0.08)
			c.draw:rect(start.wx, start.wy, input.wx, input.wy, 0x7cff8a, 1, 0.9)
		end
		-- Долгое касание стало приказом (controls/orders.luau): отпускание не выделяет.
		if input.released.left and c.shared.swallow then
			c.shared.swallow = nil
			start = nil
			return
		end
		if input.released.left then
			local picked = {}
			if moved then
				for _, e in c:within(start.wx, start.wy, input.wx, input.wy, { "Unit" }) do
					if c:mine(e) then
						table.insert(picked, e)
					end
				end
			else
				local e = c:pick(input.wx, input.wy)
				if e and c:mine(e) then
					picked = { e }
					-- Двойной щелчок: все свои того же вида на экране.
					if last.entity == e and c.time - last.time < select.data.double then
						local kind = c:proto(e)
						local x1, y1 = c:to_world(0, 0)
						local x2, y2 = c:to_world(c.screen.w, c.screen.h)
						picked = {}
						for _, other in c:within(x1, y1, x2, y2) do
							if c:mine(other) and c:proto(other) == kind then
								table.insert(picked, other)
							end
						end
					end
					last = { time = c.time, entity = e }
				end
			end
			if input.keys.shift then
				for _, e in picked do
					if not table.find(selection, e) then
						table.insert(selection, e)
					end
				end
			else
				selection = picked
			end
			if #picked > 0 then
				c:sound("click", { gain = 0.5 })
			end
			start = nil
		end
	end
	if not input.down.left then
		start = nil
	end
	if input.hits.escape then
		selection = {}
	end
	shared.selection = selection

	for _, e in selection do
		local at = c:get(e, "Position")
		if at then
			c.draw:circle(at.x, at.y, radius(c, e), select.data.ring, 1.5, 0.85)
		end
	end
end

return select
