--!strict
-- Пассажир багги: плечи и шлем в цвет стороны, миниган — светлый блок стволов с жёлтым дульным срезом.

local units = require("lib/units")

return {
	data = { title = "Стрелок", turn = 6 },
	entity = { Position = {}, Facing = {}, Owner = {}, Turret = {}, Attached = {} },
	look = units.turretLook("gunner"),
}
