--!strict
-- Параметры вида юнита для карточек и панели — перенос packages/client/src/ui/unitStats.ts: строки собираются из данных
-- вида, а не пишутся текстом — поменяются числа, поменяется и карточка. Оружейные строки добавятся с боем.

local stats = {}

stats.LABELS = {
	hp = "Прочность",
	class = "Класс",
	speed = "Скорость",
	sight = "Обзор",
	cargo = "Груз",
	repair = "Радиус ремонта",
}

stats.CLASSES = {
	infantry = "пехота",
	vehicle = "лёгкая техника",
	heavy = "тяжёлая техника",
	air = "авиация",
	building = "здания",
}

-- До одного знака после запятой, без хвостового нуля.
local function tidy(value: number): string
	local rounded = math.round(value * 10) / 10
	return if rounded == math.floor(rounded) then tostring(math.floor(rounded)) else tostring(rounded)
end

export type Row = { key: string, label: string, value: string }

-- Строки параметров вида: прочность, класс, скорость, обзор, груз, ремонт.
function stats.unit(kind): { Row }
	local d = kind.data or {}
	local rows: { Row } = {}
	local function row(key: string, value: string)
		table.insert(rows, { key = key, label = stats.LABELS[key], value = value })
	end
	if d.hp then
		row("hp", tostring(d.hp))
	end
	if d.kind then
		row("class", stats.CLASSES[d.kind] or d.kind)
	end
	if d.speed then
		row("speed", tidy(d.speed))
	end
	if d.sight then
		row("sight", tidy(d.sight))
	end
	if d.inventory then
		row("cargo", tostring(d.inventory))
	end
	if d.repair then
		row("repair", tidy(d.repair))
	end
	return rows
end

-- Параметры одним текстом, строка на параметр: для всплывающей подсказки (unitStatsText).
function stats.text(kind): string
	local lines = {}
	for _, row in stats.unit(kind) do
		table.insert(lines, row.label .. ": " .. row.value)
	end
	return table.concat(lines, "\n")
end

-- Главное одной строкой — для панели выбранного (unitStatsLine).
function stats.line(kind): string
	local brief = { hp = true, speed = true, sight = true, cargo = true, damage = true, range = true }
	local parts = {}
	for _, row in stats.unit(kind) do
		if brief[row.key] then
			table.insert(parts, row.label .. " " .. row.value)
		end
	end
	return table.concat(parts, " · ")
end

return stats
