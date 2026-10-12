/**
 * Моторы техники, синтезированные в коде: петли, которые голос мотора (motors.ts в audio) крутит быстрее или медленнее
 * вслед за оборотами. У каждого мотора две петли с одинаковым тактом — холостой ход и под нагрузкой: голос плавно
 * переходит от одной к другой, пока водитель жмёт на газ. Работает без браузера, как и synth.ts.
 */
import { attack, bandpass, decay, highpass, loop, lowpass, random, render } from './synth'

/** Сколько секунд длится петля мотора: короче — слышно, что такт повторяется. */
const LOOP_SECONDS = 2

/** Двигатель внутреннего сгорания: такт — вспышки в цилиндрах, каждая бьёт по трубам выхлопа. */
interface Combustion {
  /** Вспышек в секунду на холостом ходу: на этом такте петля звучит при скорости 1. */
  firing: number
  cylinders: number
  /** Резонансы выхлопа: частота в герцах, добротность, вес. */
  pipes: [number, number, number][]
  /** Глухой удар самой вспышки, под трубами. */
  body: number
  /** Стук дизеля: короткий звонкий щелчок в начале каждой вспышки. */
  knock: number
  /** За сколько секунд гаснет вспышка: на холостом и под нагрузкой. Под нагрузкой она резче. */
  fade: [number, number]
  /** Доля рёва горящей смеси — шума в вспышке: на холостом и под нагрузкой. */
  roar: [number, number]
}

/** Вертолёт: лопасти рубят воздух, над ними свистит турбина. */
interface Rotor {
  /** Ударов лопастей в секунду. */
  blades: number
  /** Свист турбины: частота в герцах. */
  whine: number
}

/** Квадрокоптер: несколько винтов звенят каждый на своей ноте. */
interface Props {
  notes: number[]
}

/** Реактивный: ровный рёв струи и свист компрессора. */
interface Jet {
  whine: number
}

type Design = { kind: 'combustion'; spec: Combustion } | { kind: 'rotor'; spec: Rotor } | { kind: 'props'; spec: Props } | { kind: 'jet'; spec: Jet }

export interface Motor {
  design: Design
  /** Во сколько раз обороты на пределе выше холостых: скорость петли от 1 до revs. */
  revs: number
  /** Чем звучит ход, кроме мотора: шорох шин, лязг гусениц или ничего — летающие слышны мотором. */
  running: 'roll' | 'clatter' | null
  /** Каким стартером заводится: лёгкий — бензиновый, тяжёлый — дизель. Летающие не глохнут. */
  start: 'ignition' | 'ignitionDiesel' | null
  /** Шипит ли пневмотормоз, когда махина встаёт. */
  brake: boolean
}

export const MOTORS = {
  /** Бензиновая четвёрка лёгких машин: бодрая, высоко раскручивается. */
  petrol: {
    design: {
      kind: 'combustion',
      spec: { firing: 28, cylinders: 4, pipes: [[95, 3, 1], [260, 5, 0.55], [820, 4, 0.22], [1900, 3, 0.08]], body: 0.7, knock: 0, fade: [0.004, 0.0025], roar: [0.25, 0.7] },
    },
    revs: 3.4,
    running: 'roll',
    start: 'ignition',
    brake: false,
  },
  /** Дизельная шестёрка грузовиков: ниже, стучит, раскручивается недалеко. */
  diesel: {
    design: {
      kind: 'combustion',
      spec: { firing: 20, cylinders: 6, pipes: [[70, 3, 1], [190, 4, 0.6], [560, 4, 0.28], [1300, 3, 0.1]], body: 0.9, knock: 0.45, fade: [0.005, 0.003], roar: [0.3, 0.75] },
    },
    revs: 2.4,
    running: 'roll',
    start: 'ignitionDiesel',
    brake: true,
  },
  /** Гусеничные: большой V8 — неровный рокот, самый низкий. */
  heavy: {
    design: {
      kind: 'combustion',
      spec: { firing: 16, cylinders: 8, pipes: [[52, 2.5, 1], [140, 4, 0.75], [400, 4, 0.35], [950, 3, 0.12]], body: 1.1, knock: 0.25, fade: [0.007, 0.004], roar: [0.35, 0.8] },
    },
    revs: 2.3,
    running: 'clatter',
    start: 'ignitionDiesel',
    brake: true,
  },
  /** Вертолёт: транспортник и штурмовик. */
  rotor: { design: { kind: 'rotor', spec: { blades: 13, whine: 1650 } }, revs: 1.25, running: null, start: null, brake: false },
  /** Дрон: четыре винта. */
  props: { design: { kind: 'props', spec: { notes: [190, 197.5, 205, 184.5] } }, revs: 1.6, running: null, start: null, brake: false },
  /** Бомбардировщик: турбины. */
  jet: { design: { kind: 'jet', spec: { whine: 2400 } }, revs: 1.3, running: null, start: null, brake: false },
} satisfies Record<string, Motor>

export type MotorName = keyof typeof MOTORS
export const MOTOR_NAMES = Object.keys(MOTORS) as MotorName[]

/**
 * Петля двигателя внутреннего сгорания. Вспышки идут ровно по такту, но цилиндры неодинаковы, а каждая вспышка
 * чуть сдвинута и чуть сильнее или слабее соседней — так мотор звучит живым, а не гудком. Вспышка — удар и рёв смеси,
 * которые прогоняются через трубы выхлопа. Такт петли кратен числу цилиндров: она повторяется без шва.
 */
function combustion(rate: number, spec: Combustion, load: boolean) {
  const period = 1 / spec.firing
  const count = Math.max(spec.cylinders, Math.round((LOOP_SECONDS * spec.firing) / spec.cylinders) * spec.cylinders)
  // Расписание вспышек одно на обе петли: холостая и нагруженная звучат в такт, и переход между ними не двоится.
  const next = random(spec.firing * 31 + spec.cylinders)
  const cylinders = Array.from({ length: spec.cylinders }, () => 0.7 + next() * 0.3)
  const firings = Array.from({ length: count }, (_, i) => ({ shift: next() * 0.05 * period, strength: cylinders[i % spec.cylinders] * (0.9 + next() * 0.2) }))
  const fade = spec.fade[load ? 1 : 0]
  const roar = spec.roar[load ? 1 : 0]
  const pipes = spec.pipes.map(([frequency, q, weight]) => ({ filter: bandpass(rate, frequency, q * 2), weight }))
  const thump = lowpass(rate, load ? 260 : 180)
  const flow = lowpass(rate, load ? 900 : 500)
  const knock = bandpass(rate, 2700, 3)
  const rasp = bandpass(rate, 3400, 2)
  const dc = highpass(rate, 20)
  let index = -1
  let at = 0
  let strength = 0
  return loop(rate, count * period, (t, noise) => {
    // Следующая вспышка: номер в расписании идёт по кругу, и за концом петли такт тот же, что в начале.
    const slot = Math.floor(t / period)
    const firing = firings[slot % count]
    if (slot !== index && t >= slot * period + firing.shift) {
      index = slot
      at = t
      strength = firing.strength
    }
    const since = t - at
    const n = noise()
    const kick = strength * (decay(since, fade) - decay(since, 0.0004))
    const burst = strength * n * decay(since, fade * 2.5) * roar
    // Толчок давления: волна выхлопа длиной в половину такта — из них и складывается рокот на частоте вспышек.
    const surge = since < period * 0.45 ? strength * Math.sin((Math.PI * since) / (period * 0.45)) : 0
    let pipe = 0
    for (const { filter, weight } of pipes) pipe += filter(kick + burst) * weight
    let output = pipe * 2.2 + thump(surge) * spec.body * 1.4
    // Поток газа в трубе не стихает между вспышками: тихий шум, который подталкивает каждый толчок.
    output += flow(n) * (0.25 + surge * 0.5) * (load ? 0.8 : 0.5)
    output += knock(n) * strength * decay(since, 0.0015) * spec.knock * 3
    if (load) output += rasp(n) * strength * decay(since, fade * 3) * 0.5
    return dc(output)
  })
}

/** Петля вертолёта: каждая лопасть — хлопок воздуха с низким ударом; под нагрузкой хлопки резче, турбина громче. */
function rotor(rate: number, spec: Rotor, load: boolean) {
  const period = 1 / spec.blades
  const wash = lowpass(rate, load ? 1500 : 650)
  const air = lowpass(rate, 400)
  const next = random(spec.blades)
  const strengths = Array.from({ length: Math.round(LOOP_SECONDS * spec.blades) }, () => 0.85 + next() * 0.15)
  return loop(rate, LOOP_SECONDS, (t, noise) => {
    const slot = Math.floor(t / period)
    const since = t - slot * period
    const strength = strengths[slot % strengths.length]
    const n = noise()
    const slap = wash(n) * decay(since, load ? 0.018 : 0.03) * strength * (load ? 2.6 : 1.8)
    const thump = Math.sin(2 * Math.PI * 48 * since) * decay(since, 0.03) * strength * 0.6
    const whine = (Math.sin(2 * Math.PI * spec.whine * t) + Math.sin(2 * Math.PI * spec.whine * 2 * t) * 0.4) * (load ? 0.09 : 0.05)
    return slap + thump + whine + air(n) * 0.35
  })
}

/** Петля дрона: винты звенят пилой каждый на своей ноте, биения между ними и дают жужжание роя. */
function props(rate: number, spec: Props, load: boolean) {
  const tone = lowpass(rate, load ? 3200 : 1800)
  const air = lowpass(rate, 900)
  return loop(rate, LOOP_SECONDS, (t, noise) => {
    let saw = 0
    for (const note of spec.notes) {
      const phase = (t * note) % 1
      // Лопасть — две пилы за оборот: второй гармоники больше, чем в чистой пиле.
      saw += phase * 2 - 1 + (((phase * 2) % 1) * 2 - 1) * (load ? 0.6 : 0.35)
    }
    return tone(saw) * 0.5 + air(noise()) * (load ? 0.5 : 0.3)
  })
}

/** Петля турбины: рёв струи — шум с низами, над ним свист компрессора, который чуть плавает. */
function jet(rate: number, spec: Jet, load: boolean) {
  const roar = lowpass(rate, load ? 1400 : 800)
  const deep = lowpass(rate, 120)
  return loop(rate, LOOP_SECONDS, (t, noise) => {
    const n = noise()
    const wobble = 1 + Math.sin(2 * Math.PI * 0.5 * t) * 0.004
    const whine = Math.sin(2 * Math.PI * spec.whine * wobble * t) * 0.12 + Math.sin(2 * Math.PI * spec.whine * 1.5 * wobble * t) * 0.05
    return roar(n) * (load ? 1.6 : 1.1) + deep(n) * 2 + whine * (load ? 1.4 : 1)
  })
}

/** Петля мотора name: холостой ход или под нагрузкой (load). Обе одной длины и в одном такте. */
export function motorLoop(rate: number, name: MotorName, load: boolean) {
  const { design } = MOTORS[name] as Motor
  if (design.kind === 'combustion') return combustion(rate, design.spec, load)
  if (design.kind === 'rotor') return rotor(rate, design.spec, load)
  if (design.kind === 'props') return props(rate, design.spec, load)
  return jet(rate, design.spec, load)
}

/**
 * Лязг гусениц: звенья ложатся на катки по очереди — звонкий щелчок металла и глухой удар; снизу рокочет земля.
 * На скорости 1 — 12 звеньев в секунду; голос крутит петлю вслед за ходом.
 */
function clatter(rate: number) {
  const links = 12
  const period = 1 / links
  const next = random(1201)
  const strengths = Array.from({ length: LOOP_SECONDS * links }, () => 0.5 + next() * 0.5)
  const shifts = strengths.map(() => next() * 0.15 * period)
  const plates = [bandpass(rate, 1300, 12), bandpass(rate, 2900, 16), bandpass(rate, 650, 6)]
  const weights = [1, 0.55, 0.7]
  const thud = lowpass(rate, 200)
  const ground = lowpass(rate, 110)
  return loop(rate, LOOP_SECONDS, (t, noise) => {
    const slot = Math.floor(t / period)
    const since = t - slot * period - shifts[slot % shifts.length]
    const strength = since >= 0 ? strengths[slot % strengths.length] : 0
    const n = noise()
    const hit = since >= 0 ? n * decay(since, 0.0012) * strength : 0
    let ring = 0
    for (let i = 0; i < plates.length; i++) ring += plates[i](hit) * weights[i]
    return ring * 6 + thud(hit) * 3 + ground(n) * 1.2
  })
}

/** Шорох шин: шум, глухой снизу, и щелчки гравия из-под колёс. */
function roll(rate: number) {
  const next = random(1301)
  const hum = lowpass(rate, 600)
  const deep = lowpass(rate, 90)
  const grit = bandpass(rate, 3200, 2)
  return loop(rate, LOOP_SECONDS, (_, noise) => {
    const n = noise()
    const stone = next() < 0.0012 ? 0.6 + next() * 0.4 : 0
    return hum(n) * 0.9 + deep(n) * 2 + grit(stone) * 2.5
  })
}

/** Скрип разворота на месте: траки трут грунт — шершавый скрежет с металлическим призвуком, неровный. */
function squeal(rate: number) {
  const bodies = [bandpass(rate, 900, 10), bandpass(rate, 1850, 14), bandpass(rate, 2750, 18)]
  const weights = [1, 0.6, 0.3]
  const grind = wanderer(1401)
  return loop(rate, LOOP_SECONDS, (t, noise) => {
    const scrape = noise() * (0.55 + 0.45 * grind(t * 9))
    let output = 0
    for (let i = 0; i < bodies.length; i++) output += bodies[i](scrape) * weights[i]
    return output
  })
}

/** Визг шин в крутом повороте: тон резины, дрожащий от проскальзывания, и шорох. */
function screech(rate: number) {
  const tire = bandpass(rate, 1150, 12)
  const second = bandpass(rate, 2300, 16)
  const slip = wanderer(1501)
  let phase = 0
  return loop(rate, LOOP_SECONDS, (t, noise) => {
    const n = noise()
    const wobble = 1 + 0.03 * Math.sin(2 * Math.PI * 7 * t) + 0.02 * (slip(t * 15) - 0.5)
    phase += (2 * Math.PI * 1150 * wobble) / rate
    return Math.sin(phase) * (0.3 + 0.2 * slip(t * 6)) + tire(n) * 1.5 + second(n) * 0.8
  })
}

/** Плавное число от 0 до 1, которое бродит: за единицу аргумента — шаг к новому случайному значению. */
function wanderer(seed: number) {
  const value = (step: number) => random(seed * 7919 + step + 1)()
  return (t: number) => {
    const step = Math.floor(t)
    const f = t - step
    const a = value(step)
    return a + (value(step + 1) - a) * f * f * (3 - 2 * f)
  }
}

/** Петли хода: что звучит под мотором. */
export const RUNNING = { clatter, roll, squeal, screech } satisfies Record<string, (rate: number) => Float32Array>
export type RunningName = keyof typeof RUNNING
export const RUNNING_NAMES = Object.keys(RUNNING) as RunningName[]

/**
 * Стартер: крутит мотор с подвыванием — компрессия в цилиндрах дёргает его рывками, — и мотор схватывает: вспышки
 * учащаются и садятся на холостой. heavy — дизель: стартер ниже и дольше, вспышки тяжёлые, с клубом выхлопа.
 */
function ignition(rate: number, variant: number, heavy: boolean) {
  const crank = heavy ? 0.65 : 0.42
  const whine = heavy ? 150 : 210
  const pulls = heavy ? 6.5 : 9
  const length = crank + (heavy ? 0.9 : 0.65)
  const grind = lowpass(rate, 1400)
  const pipe = bandpass(rate, heavy ? 80 : 110, 3)
  const knock = bandpass(rate, 2700, 3)
  const puff = lowpass(rate, 500)
  let phase = 0
  let fired = 0
  let at = -1
  return render(rate, length, variant, (t, noise) => {
    const n = noise()
    let output = 0
    if (t < crank + 0.05) {
      // Стартер: пила с рывками компрессии, к концу его отпускают.
      phase += (whine * (0.85 + 0.15 * Math.min(1, t / crank))) / rate
      const pull = 0.55 + 0.45 * Math.sin(2 * Math.PI * pulls * t) ** 2
      const saw = (phase % 1) * 2 - 1
      output += (saw * 0.35 + grind(n) * 0.5) * pull * attack(t, 0.02) * Math.min(1, (crank + 0.05 - t) / 0.05)
    }
    if (t >= crank) {
      // Схватил: вспышки идут всё чаще, от редких хлопков к ровному холостому.
      const local = t - crank
      const firing = (heavy ? 7 : 11) + (heavy ? 16 : 22) * (1 - decay(local, 0.25))
      if (t - at >= 1 / firing) {
        at = t
        fired++
      }
      const since = t - at
      const strength = (fired <= 3 ? 1.3 : 1) * decay(local, heavy ? 0.6 : 0.4)
      const kick = strength * (decay(since, heavy ? 0.007 : 0.004) - decay(since, 0.0004))
      output += pipe(kick + n * kick * 0.4) * 4 + kick * 0.6
      if (heavy) output += knock(n) * strength * decay(since, 0.0015) * 1.5 + puff(n) * decay(local, 0.15) * 0.9
    }
    return output
  })
}

/** Пневмотормоз: щелчок клапана и шипение сброшенного воздуха, которое темнеет и стихает. */
function airBrake(rate: number, variant: number) {
  let t = 0
  const hiss = highpass(rate, 1800)
  const tone = lowpass(rate, () => 3000 + 5000 * decay(t, 0.15))
  const click = bandpass(rate, 1400, 4)
  return render(rate, 0.8, variant, (time, noise) => {
    t = time
    const n = noise()
    return tone(hiss(n)) * attack(t, 0.006) * decay(t, 0.22) * 1.6 + click(n) * decay(t, 0.004) * 3
  })
}

/** Разовые звуки моторов: заводка и тормоз. */
export const MOTOR_SOUNDS = {
  ignition: (rate: number, variant: number) => ignition(rate, variant, false),
  ignitionDiesel: (rate: number, variant: number) => ignition(rate, variant, true),
  airBrake,
} satisfies Record<string, (rate: number, variant: number) => Float32Array>
