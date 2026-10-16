import { component } from '@kharos/ecs'
import type { BuildingType } from './buildings'
import type { Amounts, Good, Product, Resource } from './resources'
import type { TurretType } from './turrets'
import type { UnitType } from './units'
import type { WeaponType } from './weapons'
import type { PaveKind } from './paved'
import type { Stance } from './tactics'
import type { Command } from './commands'

/** Место на карте в тайлах. У здания — левый верхний тайл основания, у юнита — его центр. */
export const Position = component('Position', { x: 0, y: 0 })

/**
 * Здание. phase — сдвиг анимации в кадрах, чтобы одинаковые здания не мигали в такт. level — уровень, с единицы:
 * что он даёт, решает вид здания, см. levelFactor в buildings.ts.
 */
export const Building = component('Building', { type: 'command' as BuildingType, phase: 0, level: 1 })

/**
 * Прочность юнита или здания: value — от max до 0, в нуле юнит гибнет, а здание разрушается.
 * Сколько урона стоит за единицей, знает вид: см. UnitSpec.hp и buildingHp. max — предел: у оборонительной
 * постройки на песке он ниже единицы (SAND_DURABILITY), и чинят её только до него.
 * repairable — чинят ли это те, у кого есть Repair. regen — какую долю прочности в секунду оно восстанавливает само.
 * hit — тик последнего попадания; −1 — по этому ещё не попадали. По нему ремонт пережидает бой, см. REPAIR_PAUSE.
 */
export const Health = component('Health', { value: 1, max: 1, repairable: true, regen: 0, hit: -1 })

/**
 * Ремонтник: строит, разбирает и чинит своё — стройки, повреждённые здания и юнитов с Health.repairable. Работа
 * у него одна за раз, и юнит работает, только повернувшись к ней. radius — на сколько тайлов от его центра до края
 * цели он дотягивается; rate — сколько работы вкладывает за тик: единица — один строитель. На ходу не работает.
 * target — над чем он работает в этот тик; NONE — ни над чем.
 */
export const Repair = component('Repair', { radius: 5, rate: 1, target: -1 })

/** Чьё это. Игрок 0 — ничей: такими сущностями никто не командует. */
export const Owner = component('Owner', { player: 0 })

/**
 * Юнит. prevX, prevY — где он был тик назад: клиент рисует его между прошлым и нынешним местом.
 * facing — куда смотрит, в радианах от -π до π: 0 — вправо, растёт по часовой стрелке. prevFacing — куда смотрел тик назад.
 */
export const Unit = component('Unit', {
  type: 'infantry' as UnitType,
  prevX: 0,
  prevY: 0,
  facing: Math.PI / 2,
  prevFacing: Math.PI / 2,
})

/**
 * Путь, по которому юнит идёт; компонент есть, только пока он в пути.
 * points — оставшиеся точки в тайлах, x, y подряд. goalX, goalY — тайл, куда он шёл изначально.
 * wait — сколько тиков подряд юнит не подбирается к точке пути ближе; tries — сколько раз путь к этой точке уже прокладывался заново.
 * near — на сколько тайлов достаточно подойти к цели: так идут к тому, в кого собираются стрелять.
 */
/**
 * direct — путь не прокладывался: юнит едет к цели напрямую и объезжает встречное на ходу. stuck — такой юнит
 * упёрся и не объехал: путь ему проложит planPaths. best — ближе скольких тайлов юнит уже подбирался к нынешней
 * точке пути; −1 — ещё не мерил. Пока он подбирается ближе, wait не растёт: застрявший — не тот, кто стоит, а тот, кто
 * не приближается.
 * slot — гонящийся едет на своё место у цели, см. slotsAround в combat.ts: достав до цели раньше, он не встаёт.
 */
export const Path = component('Path', () => ({ points: [] as number[], goalX: 0, goalY: 0, wait: 0, tries: 0, near: 0, roads: false, direct: false, stuck: false, best: -1, slot: false }))

/**
 * Игрок: сущность без места на карте. Отслеживается, чтобы интерфейс узнавал о смене счёта.
 * earned — заработанная доля кредита, ещё не дошедшая до целого. rewards — какие награды игрок уже получил, по порядку.
 * away — сколько он заработал за нынешнее отсутствие, см. Rules.offlineIncome; в сети — ноль. camera — где у него
 * камера в начале игры: середина экрана в тайлах и масштаб, x, y, zoom; пусто — у его юнитов. Задаёт редактор.
 * research — какие темы игрок исследовал, по порядку; plan — какие его лаборатории ведут по очереди, первая — нынешняя;
 * progress — какая доля начатых тем уже сделана и оплачена; см. research.ts. safe — до какого тика у него защита новичка,
 * см. shelter.ts; ноль — её нет.
 */
export const Player = component('Player', () => ({ id: 0, credits: 0, earned: 0, rewards: [] as string[], away: 0, camera: [] as number[], research: [] as string[], plan: [] as string[], progress: {} as Record<string, number>, safe: 0, allies: [] as number[], head: -1, offered: [] as number[], asked: [] as number[] }), { tracked: true })

/**
 * Производство юнитов: есть у MCV и у главного здания.
 * queue — очередь заказов, первый строится сейчас; progress — сколько тиков он уже строится;
 * при нехватке энергии растёт медленнее, поэтому бывает дробным. rally — точка сбора, тайл x, y: готовые юниты
 * едут туда сами; пусто — встают у выхода.
 */
export const Producer = component('Producer', () => ({ queue: [] as UnitType[], progress: 0, rally: [] as number[] }))

/**
 * Превращение: MCV разворачивается в главное здание или здание сворачивается обратно.
 * Компонент есть, только пока оно идёт. left — сколько тиков осталось, total — сколько было всего.
 */
export const Converting = component('Converting', { left: 0, total: 0 })

/**
 * MCV послан развернуться в другом месте: едет туда и разворачивается, см. deployOrders. x, y — левый верхний тайл
 * основания главного здания; misses — сколько раз подряд не вышло подъехать. Остаётся и пока он разворачивается.
 */
export const Deploys = component('Deploys', { x: 0, y: 0, misses: 0 })

/**
 * Стройка. Пока строитель не начал работу, это только площадка: у сущности нет компонента Building,
 * она не занимает тайлы и сквозь неё ходят. С началом работы появляется Building, а Site остаётся до конца стройки.
 * progress — сколько тиков работы одного строителя уже вложено. demolish — здание не строят, а разбирают:
 * progress идёт от полного к нулю, и в нуле здание исчезает. undo — разбирают отменённую стройку: уплаченное
 * за неё возвращается по ходу разбора, а не доля цены в конце, как у готового здания.
 */
export const Site = component('Site', { type: 'generator' as BuildingType, progress: 0, demolish: false, undo: false })

/**
 * Ремонтник едет к работе site — стройке, разбору или тому, что надо починить, — чтобы она оказалась в его радиусе.
 * ordered — работу дал игрок, а не взял сам свободный строитель рядом: новую стройку из меню такой ставит в очередь.
 * misses — сколько раз подряд он не нашёл, как подъехать к работе: каждый промах вдвое реже следующая попытка.
 */
export const Builds = component('Builds', { site: 0, ordered: true, misses: 0 })

/**
 * Строитель сам везёт материал: забрать до amount груза resource со склада from и сгрузить на склад to — свою стройку
 * или, если везёт остаток назад, хранилище. from = -1 — груз уже в кузове. full — набрал и едет к to. misses — сколько
 * раз подряд не нашёл, как подъехать: после нескольких бросает поручение. См. fetching.ts.
 */
export const Fetch = component('Fetch', () => ({ from: -1, to: -1, resource: 'metal' as Good, amount: 0, full: false, misses: 0 }))

/**
 * Месторождение, из которого уже добывали: mined — сколько из него забрано; spent — выработано, и на смену ему уже
 * ждут новое, см. renewal.ts. Место — левый верхний тайл месторождения. Сами месторождения — слой карты, см. DepositLayer.
 */
export const Deposit = component('Deposit', { mined: 0, spent: false })

/**
 * Грузовик. Груз лежит в его складе (Inventory). mine — шахта, к которой его привязал игрок; -1 — свободен:
 * тогда работу ему даёт диспетчер зон (см. logistics.ts). Работа — перевезти до amount груза resource со склада
 * from на склад to; from = -1 — работы нет, to = -1 — куда везти, решится, когда наберёт груз. full — груз набран
 * и едет к to; loading — в этот тик идёт погрузка или выгрузка; waiting — уже получил приказ подъехать к лучу.
 */
export const Hauler = component('Hauler', () => ({
  mine: -1,
  from: -1,
  to: -1,
  resource: 'metal' as Good,
  amount: 0,
  full: false,
  loading: false,
  waiting: false,
  /**
   * Сколько раз подряд грузовик не нашёл, как подъехать к зданию: каждый промах вдвое реже следующая попытка,
   * чтобы недоступное здание не стоило поиска пути раз в секунду, см. seekDue в hauling.ts.
   */
  misses: 0,
  /**
   * Маршрут игрока: здания-остановки, которые грузовик объезжает по кругу вместо заявок зон; stop — к какой
   * остановке он едет сейчас. Пусто — маршрута нет. См. routes.ts.
   */
  route: [] as number[],
  stop: 0,
  /** Какие грузы грузовик возит по заявкам и маршруту; пусто — любые. */
  filter: [] as Good[],
  /** Дроп, который игрок велел вывезти: грузовик возит с него, пока там что-то лежит. -1 — такого приказа нет. */
  pickup: -1,
  /**
   * Здания, которые грузовик обслуживает: он возит только по их заявкам, а груз берёт где угодно у игрока.
   * Пусто — назначения нет. См. logistics.ts.
   */
  serve: [] as number[],
  /** Ручной приказ игрока «взять здесь, отвезти туда», см. carrying.ts; null — такого приказа нет. */
  order: null as null | { from: number; to: number; resource: Good; left: number; full: boolean },
  /**
   * Что игрок велел обеспечить правым щелчком: стройка или здание, которому нужен груз. Грузовик возит только по его
   * заявкам, беря груз где угодно у игрока, а когда заявок у него не осталось — свободен. -1 — такого приказа нет.
   */
  supply: -1,
  /**
   * Грузовик заперт: его обстроили, и выехать ему некуда — к работе он не подъедет, к какому зданию её ни дай.
   * Диспетчер такому работу не даёт, пока проход не откроют, см. hauling.ts.
   */
  trapped: false,
}))

/**
 * Склад: груз, который лежит в здании или едет в юните. items — сколько какого груза, вместе с рудой; capacity —
 * сколько помещается всего, любого груза вместе; accepts — какой груз сюда кладут, пусто — любой; limits — сколько
 * каждого груза помещается самое большее: так стройка принимает ровно свои материалы, а переработка — буфер руды.
 * reserve — резерв хранилища: столько груза оно не выдаёт никому, кроме продажи, см. givenBy в logistics.ts.
 * Между складами груз переносит транспортный луч: см. Beam и inventory.ts.
 */
export const Inventory = component('Inventory', () => ({ items: {} as Amounts, capacity: 0, accepts: [] as readonly Good[], limits: {} as Amounts, reserve: 0 }))

/**
 * Транспортный луч: переносит груз между своим складом и чужим, если между их краями не больше radius тайлов.
 * give — умеет отдавать, take — забирать; rate — сколько единиц в секунду на каждый склад. Очереди нет: луч
 * работает со всеми, кто в радиусе, сразу. links — с кем он работал в этот тик: target — чей склад, pulling —
 * забирал он с него или отдавал на него, resource — что переносил.
 */

export const Beam = component('Beam', () => ({ radius: 2, rate: 10, give: true, take: true, links: [] as { target: number; pulling: boolean; resource: Good }[] }))

/**
 * Завод изделий: recipe — какое изделие он собирает (задано видом здания, см. BuildingSpec.assembles).
 * on — включён ли он: новый завод выключен, сырьё не заказывает и не собирает. progress — сколько секунд идёт
 * нынешняя сборка, при нехватке энергии растёт медленнее; ноль — сборка не начата: сырьё ещё на складе.
 */
export const Assembly = component('Assembly', { recipe: 'blocks' as Product, on: false, progress: 0 })

/**
 * Рейс корабля космопорта. Продажа (buy = false): есть от заявки до денег; resource — что продаётся, wanted —
 * сколько; привезённое лежит в складе космопорта. Когда привезено всё, корабль улетает: left и total — сколько
 * тиков ему лететь; пока товар везут, они нулевые. Закупка (buy = true): оплачена сразу, корабль летит с самого
 * начала и, прилетев, кладёт wanted единиц в склад космопорта.
 */
export const Trade = component('Trade', { resource: 'metal' as Resource, wanted: 0, left: 0, total: 0, buy: false })

/**
 * Корабль космопорта с подарком союзнику: деньги и груз летят к его главному зданию. Всё отдано порту при
 * отправке — деньги списаны, груз забран из хранилищ, — и всё это выгрузится в точке (x, y), где стояло его
 * главное здание на момент отправки. left и total — сколько тиков лететь; done — уже выгрузил, и хост,
 * объявив об этом, снимает компонент, см. ferry.ts.
 */
export const Transfer = component('Transfer', () => ({
  to: 0,
  x: 0,
  y: 0,
  credits: 0,
  goods: {} as Amounts,
  left: 0,
  total: 0,
  done: false,
}))

/**
 * Вооружённый юнит. target — кого он атакует, -1 — никого. chase — гнаться ли за целью, когда она вне дальности:
 * так ведёт себя юнит, которому цель указал игрок или который отвечает на огонь; иначе он бьёт только тех, до кого
 * достаёт с места. cooldown — сколько тиков до следующего выстрела. stuck — сколько раз подряд гонящийся не нашёл,
 * куда идти: чем больше, тем реже он пробует снова.
 */
export const Armed = component('Armed', { target: -1, chase: false, cooldown: 0, stuck: 0, ordered: false })

/**
 * Выстрел. Position — где снаряд сейчас, prevX и prevY — где был тик назад. Пуля, ракета и ядро летят из (fromX, fromY)
 * в (toX, toY); пуля и ракета следят за целью target, ядро падает туда, где цель была при выстреле. Лазер и разряд
 * бьют сразу, и сущность — только след от них: линия из from в to. age — сколько тиков выстрел живёт, life — сколько
 * ему отпущено. player и source — чей выстрел и кто стрелял. blocked — выстрел остановила стена: бьёт он по ней,
 * а всё, что за стеной, укрыто. air — выпущен по летающему: и долетев после его гибели, он не рвётся на земле.
 */
export const Shot = component('Shot', {
  weapon: 'rifle' as WeaponType,
  player: 0,
  source: -1,
  target: -1,
  fromX: 0,
  fromY: 0,
  toX: 0,
  toY: 0,
  prevX: 0,
  prevY: 0,
  age: 0,
  life: 0,
  blocked: false,
  air: false,
})

/**
 * Взрыв: только картинка, урон уже нанесён. size — радиус в тайлах; age и life — как у выстрела; lift — на какой высоте
 * над землёй, в тайлах: сбитая ракета рвётся в воздухе.
 */
export const Blast = component('Blast', { size: 1, age: 0, life: 0, lift: 0 })

/**
 * Турель: отдельная сущность на юните-носителе, см. turrets.ts. angle — её поворот относительно носителя, в радианах
 * от -π до π: 0 — туда же, куда носитель; поворачиваясь, носитель несёт её с собой. Куда она смотрит в мире — см. turnerOf.
 * prevAngle, prevX и prevY — каким был поворот и где она была тик назад: клиент рисует её между прошлым и нынешним.
 */
export const Turret = component('Turret', { type: 'rocket' as TurretType, angle: 0, prevAngle: 0, prevX: 0, prevY: 0 })

/** Прикреплён к сущности parent: стоит на ней в along тайлов вперёд и across вправо от её центра. */
export const Attached = component('Attached', { parent: -1, along: 0, across: 0 })

/** Носитель турелей: какие турели на нём стоят. */
export const Carrier = component('Carrier', () => ({ turrets: [] as number[] }))

/**
 * Работа над покрытием тайла: фундаментом или дорогой (на болоте — мостом). Место — сам тайл. Готовое покрытие —
 * не сущность, а слой карты (sim.paving, см. paved.ts): достроенное уходит туда. work — сколько тиков работы одного
 * строителя уже вложено; remove — готовое покрытие разбирают: work идёт к нулю, и в нуле покрытие снимается со слоя,
 * а до тех пор действует; у разбора done — true. См. paving.ts.
 */
export const Pave = component('Pave', { kind: 'road' as PaveKind, work: 0, done: false, remove: false })

/** Дроп: груз, брошенный на землю, — склад (Inventory) на тайле Position без хозяина. См. drops.ts. */
export const Drop = component('Drop')

/**
 * Тактика юнита — как он ведёт себя без приказа (см. tactics.ts). stance — стойка; patrol — точки патруля x, y
 * подряд, leg — к какой он идёт; пусто — не патрулирует. away — юнит ушёл в погоню с места (homeX, homeY) и вернётся
 * туда, когда бой кончится. resupply — расстрелял магазин и сам идёт к своей сети за боеприпасами, см. supply.ts:
 * в погоню не уходит и на место вернётся полным. Без компонента юнит в обороне.
 */
export const Tactics = component('Tactics', () => ({ stance: 'defensive' as Stance, patrol: [] as number[], leg: 0, away: false, homeX: 0, homeY: 0, resupply: false }))

/**
 * Магазин юнита: сколько боеприпасов при нём. Полный — UnitSpec.ammo, выстрел тратит свою долю, пустой юнит молчит;
 * пополняется в своей сети, см. supply.ts. У юнита без компонента магазин полный.
 */
export const Magazine = component('Magazine', { ammo: 0 })

/**
 * Выключено игроком: потребитель энергии не просит её у зоны и не работает — не производит, не стреляет,
 * радар видит не дальше обычного здания. См. setWorking в assembly.ts.
 */
export const Off = component('Off', {})

/**
 * Грузовикам игрока не подъехать к этому зданию или дропу: вокруг на длину луча нет места, куда доехать. Ставит его
 * грузовик, который раз за разом не нашёл подъезда; до тика until диспетчер работу сюда не даёт, а игрок видит
 * значок. Подъедет кто-то — отметка снимается. См. hauling.ts.
 */
export const Unreachable = component('Unreachable', { until: 0 })

/**
 * Потребитель энергии занят делом и просит у зоны всю свою энергию; без метки — стоит и просит меньше. Ставит
 * хост, см. busy.ts.
 */
export const Busy = component('Busy', {})

/**
 * Генератор щита: charge — запас в единицах прочности; hit — тик последнего погашенного попадания (до паузы
 * запас не растёт); down — до какого тика купол погашен после пробоя. Ёмкость и радиус — BuildingSpec.shield.
 * См. shields.ts.
 */
export const Shield = component('Shield', { charge: 0, hit: -1, down: 0 })

/**
 * Накопитель энергии: charge — сколько энергии в нём, в единицах энергии на секунду; flow — сколько он в этот тик
 * отдаёт зоне (больше нуля) или берёт из её излишка (меньше нуля). Ёмкость и скорость — BuildingSpec.battery.
 * См. storeEnergy в income.ts.
 */
export const Accumulator = component('Accumulator', { charge: 0, flow: 0 })

/**
 * Заказ станции запроса: сколько какого готового груза она держит у себя. Грузовики и трубы довозят недостающее,
 * лишнее увозят в хранилища. См. BuildingSpec.requester и setWanted в logistics.ts.
 */
export const Wanted = component('Wanted', () => ({ items: {} as Amounts }))

/**
 * Реактор: left — сколько тиков ещё горит стержень в работе; ноль — стержня нет, и реактор не вырабатывает.
 * См. reactors.ts.
 */
export const Reactor = component('Reactor', { left: 0 })

/**
 * Взрыв, который бьёт по площади всех без разбора — своих, союзных и чужих: авария реактора, ядерный удар. Живёт
 * до ближайшего боя: урон damage до core тайлов от центра, дальше спадает до нуля на краю radius. См. fight в combat.ts.
 */
export const Burst = component('Burst', { radius: 0, damage: 0, core: 0 })

/**
 * Ракетная шахта (silos.ts). Сборка боеголовки: ticks — её срок при полной энергии, progress — сколько собрано, cost —
 * сколько кредитов списано (отмена их вернёт); ticks ноль — сборки нет. ready — боеголовка готова. start — тик приказа
 * о пуске, −1 — пуск не идёт; toX, toY — куда наведена ракета.
 */
export const Silo = component('Silo', { progress: 0, ticks: 0, cost: 0, ready: false, start: -1, toX: 0, toY: 0 })

/**
 * Ядерная ракета в полёте (silos.ts). Места на карте (Position) у неё нет: она летит над всем по прямой из (fromX, fromY)
 * в (toX, toY) с тика born, и клиент сам ведёт её по этим числам; x и y — где она сейчас. Видят её все. player — чья.
 * mark — метка точки удара (Mark): есть весь полёт. hunted — тик, когда её собьёт уже выпущенный перехватчик, −1 — по ней
 * не стреляли. blind — только у копии чужой ракеты на клиенте: (toX, toY) у неё не точка удара, а точка на курсе,
 * см. seenBy в wire.ts.
 */
export const Missile = component('Missile', { fromX: 0, fromY: 0, toX: 0, toY: 0, born: 0, x: 0, y: 0, player: 0, mark: -1, hunted: -1, blind: false })

/**
 * Перехватчик (silos.ts): сборка ракеты-перехватчика, как боеголовки у шахты, — ticks её срок при полной энергии,
 * progress — сколько собрано, ticks ноль — сборки нет; missiles — сколько готовых ракет ждут чужую ядерную.
 */
export const Interceptor = component('Interceptor', { progress: 0, ticks: 0, missiles: 0 })

/**
 * Ракета-перехватчик в полёте (silos.ts): как у ядерной, места на карте нет — летит из (fromX, fromY) с тика born
 * и встречает ядерную ракету missile над точкой (toX, toY) на высоте lift в тик arrive; тогда обе гибнут. player — чей
 * перехватчик.
 */
export const Chaser = component('Chaser', { fromX: 0, fromY: 0, toX: 0, toY: 0, lift: 0, born: 0, arrive: 0, player: 0, missile: -1 })

/**
 * Метка на карте, которую видят не по обзору, а по списку viewers: место ядерного пуска (kind launch) — игроки
 * с радаром, точка удара (strike) — те, чьё под ней. player — чья ракета: ему и его союзникам метка видна всегда.
 * Сигнал союзникам (ping) — метка без viewers: её видят только сам игрок и союзники, см. pings.ts.
 * until — тик, когда она пропадает. См. silos.ts.
 */
/**
 * Вспышка ядерного взрыва: до тика until всем игрокам виден круг radius вокруг неё — туман там на миг снят у каждого,
 * где бы он ни был. См. silos.ts и vision.ts.
 */
export const Glare = component('Glare', { radius: 0, until: 0 })

export const Mark = component('Mark', () => ({ kind: 'launch' as 'launch' | 'strike' | 'ping', until: 0, player: 0, viewers: [] as number[] }))

/**
 * Очередь приказов юнита, отданных с Shift: следующий он берёт, когда закончит нынешнее дело. group — общий номер
 * приказа, отданного нескольким юнитам сразу: такой приказ они берут вместе, чтобы встать строем. См. orders.ts.
 */
export const Orders = component('Orders', () => ({ list: [] as { group: number; command: Command }[] }))

/**
 * Обречённый: игрок сдался, и через left тиков юнит или здание взорвётся. scuttled — игрок уничтожил юнит сам, а
 * не сдался: о капитуляции тут объявлять нечего. См. defeat.ts.
 */
export const Doomed = component('Doomed', { left: 0, scuttled: false })

/**
 * Пачка груза в трубах: до BATCH единиц resource едут из склада from на склад to по пути path — узлам графа сети
 * (откуда, повороты, куда), 1 тайл за тик. sent — тик отправки, arrive — тик прихода. push — пачку отправил
 * поставщик (развозит готовое по хранилищам), а не заказчик. Пачка живёт до тика после прихода: до тех пор маршрут
 * занят. Шлётся только хозяину, см. shownTo. См. pipes.ts.
 */
export const Batch = component('Batch', () => ({ from: -1, to: -1, resource: 'metal' as Good, amount: 0, sent: 0, arrive: 0, push: false, path: [] as number[] }))

/**
 * Улучшение: готовое здание поднимают на следующий уровень. Компонент есть, только пока оно идёт; здание при этом
 * работает как обычно. progress — сколько тиков работы одного строителя уже вложено. Цена списана при заказе;
 * scale — по какому Rules.upgradeScale: отмена возвращает уплаченное, и срок работы — от него же. См. upgrades.ts.
 */
export const Upgrade = component('Upgrade', { progress: 0, scale: 1 })

/**
 * Как работает лаборатория, см. research.ts: speed — с какой долей полной скорости она вела тему в последний тик,
 * lack — почему стоит или замедлилась (LabLack), пусто — работает в полную силу. Сама тема — в плане игрока.
 */
export const Research = component('Research', { speed: 0, lack: '' })

/** Ворота, см. gates.ts: open — створка поднята, через них ходят. Компонент появляется у готовых ворот. */
export const Gate = component('Gate', { open: false })

/**
 * Разведка радара, см. radar.ts: (x, y) — середина последней вспышки, tick — когда она случилась (−1 — ещё не было);
 * wait — сколько тиков до следующей. Обзор хозяина включает круг вспышки, пока она не погасла. dark — радару не
 * хватает энергии: постоянно он видит как обычное здание, см. buildingSight.
 */
export const Scan = component('Scan', { x: 0, y: 0, tick: -1, wait: 0, dark: false })

/**
 * Землеройная работа, см. earthworks.ts: строители срывают гору (peak) или сглаживают кромку обрыва (cliff). Место —
 * тайл кромки или тайл вершины горы; (peakX, peakY) — вершина, по ней находятся все тайлы горы. cost — сколько за
 * работу заплачено (отмена его вернёт), work — сколько тиков работы одного строителя уже вложено.
 */
export const Dig = component('Dig', { kind: 'peak' as 'peak' | 'cliff', cost: 0, work: 0, peakX: 0, peakY: 0 })

/** Компоненты, которые попадают в сохранение и в сеть. Новый компонент симуляции добавляй сюда. */
/**
 * Призрак: чужое здание или месторождение, каким клиент видел его последний раз. Живёт только в клиенте: хост
 * о нём не знает и не шлёт, пока место в тумане. Пропадает, когда место снова в обзоре, а здания там нет.
 * В редакторе призрак — юнит под указателем, который встанет по щелчку; blocked — встать ему там нельзя.
 */
export const Ghost = component('Ghost', { blocked: false })

export const SAVED = [Position, Building, Health, Repair, Turret, Attached, Carrier, Owner, Unit, Path, Player, Producer, Converting, Deploys, Site, Builds, Fetch, Deposit, Hauler, Inventory, Beam, Assembly, Trade, Transfer, Armed, Shot, Blast, Pave, Drop, Tactics, Doomed, Orders, Off, Busy, Batch, Upgrade, Research, Scan, Gate, Shield, Accumulator, Reactor, Burst, Silo, Missile, Interceptor, Chaser, Mark, Glare, Dig, Unreachable, Magazine, Wanted]
