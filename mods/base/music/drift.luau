--!strict
-- Спокойная музыка партии: аккорды подложкой, бас на сильные доли, редкая мелодия из пентатоники. Ночью — другой
-- ряд аккордов, вместо щипков — колокол, тише и реже. Сочинитель зовётся раз в такт; случайность — от номера такта,
-- так что музыка одинакова при каждом запуске.

local drift = {
	tempo = 66,
	beats = 4,
}

local chords = {
	calm = { { 57, 60, 64 }, { 53, 57, 60 }, { 48, 52, 55 }, { 55, 59, 62 } },
	night = { { 57, 60, 64 }, { 52, 55, 59 }, { 53, 57, 60 }, { 50, 53, 57 } },
}

local scale = { 57, 60, 62, 64, 67, 69, 72, 74, 76 }

local function rand(bar: number, i: number): number
	local x = math.sin(bar * 12.9898 + i * 78.233) * 43758.5453
	return x - math.floor(x)
end

function drift.compose(bar: number, c)
	local mood = c.music.mood or "calm"
	local list = chords[mood] or chords.calm
	local chord = list[bar % #list + 1]
	local notes = {}
	for _, note in chord do
		table.insert(notes, { beat = 0, length = 4, voice = "pad", note = note, velocity = 0.5 })
	end
	table.insert(notes, { beat = 0, length = 1.5, voice = "bass", note = chord[1] - 24, velocity = 0.7 })
	if mood ~= "night" then
		table.insert(notes, { beat = 2, length = 1.5, voice = "bass", note = chord[1] - 24, velocity = 0.5 })
	end
	-- Каждый восьмой такт — передышка без мелодии.
	if bar % 8 == 7 then
		return notes
	end
	local count = if mood == "night" then 2 else 4
	for i = 1, count do
		if rand(bar, i) > 0.35 then
			local note = scale[math.floor(rand(bar, i + 20) * #scale) + 1]
			table.insert(notes, {
				beat = math.floor(rand(bar, i + 10) * 8) / 2,
				length = 0.5,
				voice = if mood == "night" then "bell" else "pluck",
				note = note + (if mood == "night" then 12 else 0),
				velocity = 0.35 + rand(bar, i + 30) * 0.3,
				pan = rand(bar, i + 40) * 1.2 - 0.6,
			})
		end
	end
	return notes
end

return drift
