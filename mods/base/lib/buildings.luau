--!strict
-- Чертежи зданий — перенос packages/client/src/game/buildings/buildingArt.ts: ART_TILE пикселей на тайл, как у
-- местности; начало координат — левый верхний угол основания, высокие части уходят в y < 0. Кадров в цикле анимации —
-- FRAMES, чертёж получает фазу цикла t от 0 до 1 и обязан замыкаться при t = 1. Огни кадра — canvas:light.

local buildings = {}

buildings.TILE = 16
buildings.FRAMES = 16
-- Поля вокруг основания в пикселях спрайта: место для высоких частей.
buildings.PAD = 16

-- Тона от тёмного к светлому. Свет падает слева сверху.
buildings.INK = 0x0b111b
buildings.STEEL = { [0] = 0x1c2b3e, 0x2d4560, 0x41617f, 0x6184a3, 0x9bb9d1 }
buildings.IRON = { [0] = 0x1e2024, 0x3a3d43, 0x5b5f66, 0x8b9097, 0xc3c7cc }
buildings.RUST = { [0] = 0x5e2f24, 0x9a523a, 0xc47a57 }
-- Цвет команды: от погашенного огня к горящему.
buildings.TEAM = { [0] = 0x10358f, 0x1f63d8, 0x5aa9ff, 0xe4f4ff }
buildings.HAZARD = { [0] = 0x1a1608, 0xd9b021 }
buildings.DARK = 0x04070b

local INK, TEAM = buildings.INK, buildings.TEAM
local TURN = math.pi * 2

-- Плавное мигание: 1 при t = offset, 0 через полцикла.
function buildings.pulse(t: number, offset: number?): number
	return 0.5 + 0.5 * math.cos(TURN * (t - (offset or 0)))
end

-- Бегущий огонь: вспыхивает при t = offset и быстро гаснет.
function buildings.chase(t: number, offset: number): number
	local phase = ((t - offset) % 1 + 1) % 1
	return math.max(0, 1 - phase * 3)
end

function buildings.teamColor(level: number): number
	return TEAM[math.min(3, math.floor(level * 4))]
end

-- Плита с обводкой, фаской и передней стенкой высотой wall. Размер h — вместе со стенкой.
function buildings.slab(g, x: number, y: number, w: number, h: number, wall: number, tones)
	local top = h - wall - 2
	g:rect(x, y, w, h, INK)
	g:rect(x + 1, y + 1, w - 2, top, tones[1])
	g:rect(x + 1, y + 1, w - 2, 1, tones[3])
	g:rect(x + 1, y + 2, 1, top - 1, tones[2])
	g:rect(x + w - 2, y + 2, 1, top - 1, tones[0])
	g:rect(x + 1, y + 1 + top, w - 2, wall, tones[0])
end

-- Купол: тон светлеет к левому верхнему краю.
function buildings.dome(g, cx: number, cy: number, r: number, tones)
	g:circle(cx, cy, r + 1, INK)
	g:circle(cx, cy, r, tones[0])
	g:circle(cx - r * 0.12, cy - r * 0.12, r * 0.82, tones[1])
	g:circle(cx - r * 0.28, cy - r * 0.28, r * 0.52, tones[2])
	if r >= 4 then
		g:circle(cx - r * 0.42, cy - r * 0.42, r * 0.2, tones[3])
	end
end

-- Круглая башня: основание в (cx, cy), купол поднят на height.
function buildings.tower(g, cx: number, cy: number, r: number, height: number, tones)
	g:circle(cx, cy, r + 1, INK)
	g:rect(cx - r - 1, cy - height, r * 2 + 2, height, INK)
	g:circle(cx, cy, r, tones[0])
	g:rect(cx - r, cy - height, r * 2, height, tones[0])
	g:rect(cx - r + 1, cy - height, math.max(1, math.floor(r / 2)), height + 1, tones[1])
	buildings.dome(g, cx, cy - height, r, tones)
end

-- Круглая лампа цвета команды; светит на землю.
function buildings.lamp(g, cx: number, cy: number, r: number, level: number)
	g:light(cx, cy, r, level)
	g:circle(cx, cy, r + 1, INK)
	g:circle(cx, cy, r, buildings.teamColor(level * 0.74))
	g:circle(cx - r * 0.3, cy - r * 0.3, r * 0.5, buildings.teamColor(0.26 + level * 0.74))
end

-- Огонёк 2×2 с левым верхним углом в (x, y).
function buildings.bulb(g, x: number, y: number, level: number)
	g:rect(x, y, 2, 2, buildings.teamColor(level))
	g:light(x + 1, y + 1, 1, level)
end

-- Облик здания width × height тайлов: draw(canvas, t, working) — чертёж; working — есть ли у здания вид работы.
function buildings.look(width: number, height: number, draw: (any, number, boolean) -> (), working: boolean?)
	local pad, tile, frames = buildings.PAD, buildings.TILE, buildings.FRAMES
	return {
		size = { width * tile + pad * 2, height * tile + pad * 2 },
		origin = { pad, pad },
		-- Место сущности — центр основания.
		anchor = { width * tile / 2, height * tile / 2 },
		directions = 1,
		phases = if working then frames * 2 else frames,
		fps = 8,
		layer = "building",
		shadow = { 3, 0.4 },
		draw = function(canvas, _angle: number, phase: number)
			draw(canvas, (phase % frames) / frames, phase >= frames)
		end,
	}
end

return buildings
