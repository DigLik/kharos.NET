--!strict
-- Звук работы зданий — перенос packages/client/src/game/machines.ts. Каждый вид звука — одна петля, сколько бы ни
-- звучало источников: громкость — по тому, сколько их и насколько они близки к середине экрана, сторона — по тому, где
-- они. Звучит только своё работающее: цех стучит, пока движется его работа.

local PERIOD = 0.2
local LEVELS = { hum = 0.22, machinery = 0.25, drill = 0.2 }
local SATURATE = 3
local WORK_HOLD = 0.6
local HEARING = 0.6

local since = PERIOD
local clock = 0
local activity = {}

-- Работает ли здание: то, что в нём меняется, менялось не раньше WORK_HOLD секунд назад.
local function busy(entity: number, value: number): boolean
	local state = activity[entity] or { value = value, untilAt = 0 }
	if math.abs(value - state.value) > 1e-9 then
		state.untilAt = clock + WORK_HOLD
	end
	state.value = value
	activity[entity] = state
	return clock < state.untilAt
end

return {
	update = function(c)
		if not c.audio then
			return
		end
		clock += c.dt
		since += c.dt
		if since < PERIOD then
			return
		end
		since = 0
		local sum, side = {}, {}
		local function add(name: string, x: number, y: number)
			local near = c:nearness(x, y, HEARING)
			if near <= 0 then
				return
			end
			sum[name] = (sum[name] or 0) + near
			side[name] = (side[name] or 0) + near * c:side(x)
		end
		local seen = {}
		for _, e in c:each({ "Building", "Position", "Owner" }) do
			seen[e] = true
			if not c:mine(e) then
				continue
			end
			local at = c:get(e, "Position")
			local kind = c:proto(e)
			if kind and kind.data and (kind.data.power or 0) > 0 then
				add("hum", at.x, at.y)
			end
			local producer = c:get(e, "Producer")
			if producer and producer.unit ~= "" and busy(e, producer.progress) then
				add("machinery", at.x, at.y)
			end
		end
		for e in activity do
			if not seen[e] then
				activity[e] = nil
			end
		end
		for name, level in LEVELS do
			local total = sum[name] or 0
			c.audio:loop(name, level * (1 - math.exp(-total / SATURATE)), if total > 0 then side[name] / total else 0)
		end
	end,
}
