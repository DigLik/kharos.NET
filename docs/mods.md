# Моды: формат и API

Справочник мододела: как устроен мод, что отдаёт вид и что движок даёт скриптам. Зачем так — `docs/content.md`.
Образец — мод `mods/base`: это и есть игра Kharos.

## Мод

Мод — папка в `mods/`. В корне — `mod.luau`, остальное — разделы: папка первого уровня — раздел, в нём вид — файл
`имя.luau` или папка `имя/` с `init.luau`. Папка `lib/` — не раздел, а общий код для `require`.

```luau
-- mods/extra/mod.luau
return {
	title = "Ещё юниты",
	version = "0.1.0",
	requires = { "base" },        -- раньше этого мода; "?имя" — необязательная зависимость
	patch = function(protos)      -- после загрузки всех модов, в их порядке
		protos.units.buggy.data.speed = 5
		protos.moves.hover = nil
	end,
}
```

Порядок модов — зависимости раньше, при равенстве по имени. Вид с тем же именем из более позднего мода заменяет
прежний. `patch` правит, добавляет и убирает чужие виды — то, что у Factorio `data-updates`.

`require("lib/geom")` — от корня своего мода, `require("./look")` — от папки файла, `require("@base/lib/geom")` — из
другого мода. Модуль выполняется один раз; вид, взятый `require`, — та же таблица, что в `protos`.

Глобальные таблицы только для чтения (песочница): без этого `math` и `buffer` медленнее в 16 раз. Глобальная `SIDE` —
`"host"` или `"client"`.

## Вид

Файл вида возвращает таблицу. Движок сам вписывает в неё `name`, `section`, `id` (номер, одинаковый у всех с теми же
модами) и `source`. Остальное — по договорённости разделов; что читает движок:

| Поле | Сторона | Что это |
|---|---|---|
| `data` | обе | числа и строки вида: скорость, цена, размер |
| `components` | хост | объявления компонентов (ниже) |
| `entity` | хост | из чего сделана сущность вида: таблица или функция от вида |
| `systems` | хост | системы над столбцами |
| `on` | хост | обработчики событий |
| `commands` | хост | приказы игроков |
| `look` | клиент | облик: чертёж, огни, звук |

Хост и клиент выполняют одни и те же файлы, каждый в своей машине, и берут свои поля. Чтобы правки данных другими
модами доходили до всего, пишите `entity` функцией и обращайтесь к данным через таблицу вида:

```luau
local buggy = {}
buggy.data = { speed = 4, turn = 3 }
buggy.entity = function(kind)
	return { Position = {}, Facing = {}, Owner = {}, Drive = { move = "wheels", speed = kind.data.speed } }
end
return buggy
```

## Хост

### Компоненты

```luau
components = {
	Drive = { move = "", speed = 0, turn = 0, tx = 0, ty = 0, moving = false },
	Producer = { unit = "", progress = 0, queue = {} },
}
```

Вид поля — по значению по умолчанию: число, флаг (`true`/`false`), строка, таблица. Числа, флаги и строки лежат
столбцами f64 (строка — номером, `ctx:id` и `ctx:text`); таблица — значение целиком, только через `ctx:get`/`ctx:set`.
Один компонент можно объявлять в нескольких видах: поля складываются.

Компоненты движка: `Position { x, y }` (центр, в тайлах), `Facing { angle }`, `Owner { player }`, `Proto { id }`
(вид сущности; ставится сам при `ctx:spawn`).

### Системы

```luau
systems = {
	drive = {
		query = { "Position", "Facing", "Drive" },  -- сущности со всеми этими компонентами
		where = { Drive = { move = "wheels" } },    -- и с такими значениями полей (числа, флаги, строки)
		write = { "Position", "Facing", "Drive" },  -- чьи столбцы после системы вернуть в мир
		every = 1,                                  -- раз в сколько тиков
		after = "income", before = { "fight" },     -- ключ системы или полное имя "moves/wheels.drive"
		run = function(q, ctx, kind)
			for i = 0, q.n - 1 do
				local at = i * 8
				local x = buffer.readf64(q.Position.x, at)
				buffer.writef64(q.Position.x, at, x + ctx.dt)
			end
		end,
	},
}
```

`q.n` — сколько сущностей, `q.entity` — их номера, `q.Компонент.поле` — буфер f64 на каждое числовое поле. Писать
можно в любой буфер, в мир вернутся только компоненты из `write`. Порядок систем — по `after`/`before`, иначе по
номерам видов. Время каждой системы по частям — `Host::timings`.

Замер (`cargo run -p kharos-world --release --example bench [юнитов]`, Apple Silicon): 10 тыс. багги на колёсах с новой
целью доехавшему — 0,9 мс на тик, из них Luau ходовой — 0,49, сборка и возврат столбцов — 0,37; 100 тыс. — 8,9 мс.
С расталкиванием (`mechanics/crowd`) — 2,1 и 34 мс.

### ctx

| | |
|---|---|
| `ctx.tick`, `ctx.dt` | номер тика и его длина в секундах |
| `ctx.storage` | память модов: обычная таблица, уходит в сохранение |
| `ctx.protos`, `ctx.game` | все виды по разделам; вид идущей партии |
| `ctx:spawn(вид, поля?)` | новая сущность: `"units/buggy"`, таблица вида или номер; `nil` — без вида. Поля — `{ Position = { x = 1 } }` поверх `entity`. Номер — сразу |
| `ctx:destroy(e)`, `ctx:add(e, К, поля?)`, `ctx:set(e, К, поля)`, `ctx:remove(e, К)` | изменения — после системы, по порядку |
| `ctx:get(e, К)`, `ctx:has(e, К)`, `ctx:alive(e)` | мир сейчас (буферы идущей системы сюда ещё не вернулись) |
| `ctx:query({ К… })` | та же таблица столбцов, на чтение |
| `ctx:near(x, y, r, { К… }?)` | сущности с Position в круге, по номерам |
| `ctx:count(К)` | сколько сущностей с компонентом |
| `ctx:id(строка)`, `ctx:text(номер)` | номер строки для сравнения со столбцом и обратно |
| `ctx:proto(вид)` | таблица вида по номеру или `"раздел/имя"` |
| `ctx:emit(имя, данные?)` | событие видам: `on[имя](ctx, данные)` после системы |
| `ctx:signal(имя, данные?)` | сигнал клиентам: звук, вспышка, сообщение |
| `ctx:random()`, `ctx:random(n)`, `ctx:random(a, b)` | случайное, одинаковое у всех, кто считает тот же мир |
| `ctx:separate(q, сила?)` | пакетный инструмент: расталкивает сущности запроса по `Unit.radius`, пишет в `q.Position` |

Пакетные инструменты — то, что горячо над тысячами сущностей: движок делает это над буферами запроса, а система,
которая решает, когда и кого, остаётся контентом. `ctx:separate` на 10 тыс. юнитов — 1,2 мс.

### События и приказы

`on = { имя = function(ctx, данные) end }`. События движка: `start { game }` — начало партии, `player_joined { player }`.

`commands = { имя = function(ctx, игрок, аргументы) end }` — приказ игрока, выполняется в начале тика. Проверять
аргументы и права — дело обработчика: приходит что угодно.

### Партия

Вид из `games/` — что такое партия. Правила (`systems`, `on`, `commands`) берутся только у идущей партии; `start(ctx,
партия)` — в начале. `client` — что берёт подача: камера, управление, панели, атмосфера, музыка (имена видов своих
разделов).

## Клиент

Клиент (`apps/client`, `cargo run -p kharos-client --release`) выполняет те же файлы в своей машине и берёт подачу.
Мир он видит только на чтение, менять может только приказом. Свою память скрипт держит в локальных переменных модуля:
машина клиента одна на игрока.

Порядок кадра: сигналы хоста → `signals` видов; камера; панели (`ui/`); управление (`controls/`); атмосфера; звук;
музыка. Что берётся — решает партия:

```luau
skirmish.client = {
	camera = "rts",                 -- cameras/rts.luau
	controls = { "select", "orders" },
	ui = { "hud" },
	atmosphere = { "day", "wind" },
	music = "drift",
	teams = { { 0x1f63d8, 0x5aa9ff, 0xe4f4ff }, … },  -- тёмный, основной, светлый цвет стороны по номеру игрока
	ground = { sand = 0xb08a5c, rock = 0x6b6058, edge = 0x2a2420 },
}
```

### Облик

```luau
look = {
	frame = 32, directions = 64, phases = 2, fps = 8, animate = "moving",  -- или "always"
	draw = function(canvas, angle, phase) … end,
	lights = { lamps = { { вдоль, поперёк, радиус } }, beam = { along, length, near, spread, level }, glows = { { x, y, радиус } } },
	sound = { voice = "motor", gain = 0.5, control = function(knobs, unit, c) … end },
	icon = { direction = 56 },
}
```

Чертёж выполняется при загрузке на каждое направление и фазу — получается атлас. Холст (`canvas`) — в пикселях спрайта
(16 на тайл), начало — центр: `circle(x, y, r, цвет)`, `ring(x, y, r, толщина, цвет)`, `line(ax, ay, bx, by,
толщина, цвет)`, `rect(x, y, w, h, угол, цвет)`, `poly({ x1, y1, … }, цвет)`, `pixel(x, y, цвет)`, `team(тон)` — цвет
стороны (0 тёмный, 1 основной, 2 светлый), `canvas.shade(цвет, k)`. Цвет — `0xRRGGBB`.

Огни (в пикселях спрайта от центра, вдоль — вперёд по направлению) горят, когда атмосфера даёт `lamps`. Звук облика
звучит у ближайших к камере (до восьми разом): `control` ставит ручки голоса, `unit` — `{ entity, load, moving }`.

### c

| | |
|---|---|
| `c.input` | `x`, `y` — экран; `wx`, `wy` — мир; `down`, `pressed`, `released` — по `left`, `right`, `middle`; `wheel`; `keys` — зажатые (`"a"`, `"1"`, `"shift"`, `"left"`, `"escape"`); `hits` — нажатые за кадр; `text`; `over_ui`; `touch`; `focused`; `inside` |
| `c.camera` | `x`, `y` — центр в тайлах, `zoom` — пикселей на тайл |
| `c.screen` | `w`, `h` в логических пикселях |
| `c.shared` | общая память скриптов клиента (`selection` — выделенное) |
| `c.atmosphere` | `light = { r, g, b }`, `lamps` 0..1, `wind = { x, y }`, `clock` — то, что пишет атмосфера |
| `c.music` | `mood`, `volume` |
| `c.time`, `c.dt`, `c.tick`, `c.player`, `c.protos`, `c.game` | |
| `c:each({ К… })`, `c:get(e, К)`, `c:has`, `c:alive`, `c:proto(e)`, `c:mine(e)` | мир; `Position` и `Facing` сглажены между тиками |
| `c:pick(x, y)`, `c:within(x1, y1, x2, y2, { К… }?)` | сущность под точкой мира, сущности в прямоугольнике |
| `c:to_screen(x, y)`, `c:to_world(x, y)` | |
| `c:send(приказ, аргументы)` | приказ хосту (`commands` видов) |
| `c:sound(голос, { x, y, gain, pitch, bus }?)` | короткий звук; с `x, y` — на месте в мире |
| `c:particle({ x, y, vx, vy, life, color, size, drift })` | частица; `drift` — сносит ветром |
| `c.draw:line/circle/rect(…, цвет, толщина?, альфа?)` | поверх мира, в тайлах; толщина — в пикселях |
| `c.ui:panel/rect/outline/line/circle/text/measure/button/area/icon/bar` | интерфейс в пикселях экрана; `button(ключ, x, y, w, h, подпись?, { enabled, selected, size })` → `clicked, hovered`; `icon(вид, x, y, размер, сторона?)` |

Цвет поверх мира и в интерфейсе — `0xRRGGBB` или `0xAARRGGBB`.

### Разделы подачи

- `cameras/` — `start(cam, c)`, `update(cam, c)`.
- `controls/` — `update(c)`; `ui/` — `draw(c)` (раньше управления: панель забирает указатель).
- `atmosphere/` — `update(a, c, knobs)`; `voices = { ключ = "голос" }` — звуки, которые звучат всё время, а `knobs.ключ`
  — их ручки (плюс `gain`, `pan`).
- `music/` — `tempo`, `beats`, `compose(такт, c)` → ноты `{ beat, length, voice, note (MIDI), velocity, gain, pan }`.
- `signals = { имя = function(c, данные) end }` в любом виде — ответ на `ctx:signal` хоста.

### Звук

Голос (`sounds/`) — схема узлов Web Audio, которую строит Luau, а звук считает движок:

```luau
return {
	graph = function(s)            -- s.at, s.length (nil — пока держат), s.note (Гц), s.velocity, s.rate
		local osc = s:osc("sawtooth", 42)
		local filter = s:filter("lowpass", 320, 4)
		s:lfo(9, 2.5):to(osc.frequency)
		osc:to(filter):to(s:env({ attack = 0.01, release = 0.3 })):to(s:out())
		return { osc = osc, filter = filter }   -- получит update
	end,
	update = function(n, knobs) n.filter.frequency = 260 + knobs.load * 1100 end,
}
```

Узлы: `osc(волна, частота)`, `noise("white"|"pink"|"brown")`, `const(v)`, `filter(вид, частота, q)`, `gain(v)`,
`delay(время)`, `shape(сила)`, `pan(v)`, `lfo(частота, размах)`, `env({ attack, decay, sustain, release, peak })`,
`out()`. `a:to(b)` соединяет (и с параметром: `a:to(osc.frequency)`). Параметры: `узел.частота = v` — плавно,
`узел.frequency:set(v, когда?)`, `:ramp(v, когда)`, `:exp(v, когда)`, `:target(v, когда, tau)`. Запечённый звук —
`bake = { length, render = function(out, rate, n) end }`: `render` пишет `n` отсчётов f32 в буфер один раз при загрузке.
Шины: `sfx`, `units`, `ambience`, `music`.
