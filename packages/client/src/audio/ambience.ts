import { loop, type Noises } from './flight'
import { random, type DistantName } from './synth'

/**
 * Каким фон должен быть сейчас: погода у игрока, высота камеры и обстановка. Обстановку фон сам сглаживает — её
 * числа могут скакать от кадра к кадру, а звук меняется медленно, см. ease.
 */
export interface Mood {
  /** Ветер: 0 — штиль, 1 — сильный. */
  wind: number
  /** Осадки: 0 — ясно, 1 — буря. */
  precipitation: number
  /** Освещение: 0 — ночь, 1 — полдень. */
  light: number
  /** Высота камеры: 0 — у самой земли, 1 — выше всего. */
  altitude: number
  /** Тревога: 0 — противника не видно, 1 — его много или на нас летит ракета. */
  threat: number
  /** Бой: 0 — тихо, 1 — свои стреляют и по своим стреляют вовсю. */
  battle: number
  /** Биомы под камерой: доля каждого по номеру Biome, вместе — единица. */
  biomes: readonly number[]
  /** Счёт успехов игрока: растёт с каждым — открыта тема, отбита атака. Важен не он, а то, что он вырос. */
  triumphs: number
}

/** Громкости слоёв фона. Все тихие: фон слышен, когда в игре затишье, и уходит под бой. */
const WIND_LEVEL = 0.14
const HOWL_LEVELS = [2.4, 1.5]
const HISS_LEVEL = 0.22
const PAD_LEVEL = 0.017
const DISTANT_LEVEL = 0.18
const REVERB_LEVEL = 0.55
/** За сколько секунд фон проявляется после первого нажатия игрока. */
const FADE_IN = 6
/** Как часто фон подстраивается под погоду, в секундах: параметры меняются плавно, чаще не нужно. */
const STEP = 0.1
/** Гулы ветра в развалинах: середина полосы в герцах и насколько она бродит, в долях. */
const HOWLS = [
  { frequency: 420, drift: 0.25, q: 9 },
  { frequency: 780, drift: 0.2, q: 14 },
]
/**
 * Аккорды подложки: ре минор — си-бемоль — до — ля минор, медленно и по кругу. Ноты — номера MIDI; первая — бас,
 * он держится весь аккорд, остальные по одной всплывают и тают.
 */
const CHORDS = [
  [38, 57, 62, 65, 69, 76],
  [34, 53, 57, 62, 65, 72],
  [36, 55, 60, 64, 67, 74],
  [33, 52, 57, 60, 64, 71],
]
/** Сколько секунд звучит аккорд. */
const CHORD_TIME = 24
/** Как часто всплывает нота подложки, в секундах: от и до. */
const NOTE_GAP = [2.5, 5]
/** Как часто случается что-то вдалеке, в секундах: от и до. */
const DISTANT_GAP = [9, 26]

/**
 * За сколько секунд фон входит в обстановку и выходит из неё: тревога и бой нарастают быстро, а отпускают медленно,
 * чтобы короткая стычка не обрывала звук; биом под камерой меняется за десятки секунд — пролёт над картой его не
 * сдвинет. Это постоянные времени сглаживания: за столько секунд пройдено две трети пути.
 */
const EASE = {
  threat: { rise: 6, fall: 30 },
  battle: { rise: 4, fall: 25 },
  biome: { rise: 25, fall: 25 },
} as const
/** Пульс тревоги и боя: тихий низкий удар с отголоском, как сердце. Громкость и через сколько секунд следующий — в тревоге и в бою. */
const PULSE_LEVEL = 0.11
const PULSE_BEAT = [1.7, 0.8]
/** Отголосок пульса: через сколько секунд после удара и во сколько тише. */
const PULSE_ECHO = 0.28
const PULSE_ECHO_LEVEL = 0.45
/** Гул боя: низкая пила сквозь глухой фильтр, держится под всем боем. */
const DRONE_LEVEL = 0.025
/** Сколько секунд светлеют аккорды после успеха. */
const TRIUMPH_SECONDS = 40
/** Окраска нот по биому: сколько добавляют нижняя октава эрга, звон солончаков и дребезг красных пустошей. */
const ERG_SUB = 0.5
const SALT_BELL = 0.35
const RED_GRIT = 0.12

const midi = (note: number) => 440 * Math.pow(2, (note - 69) / 12)
const clamp01 = (value: number) => Math.max(0, Math.min(1, value))

/** Шаг сглаживания: value идёт к target с постоянной времени rise вверх и fall вниз, за seconds секунд. */
const ease = (value: number, target: number, seconds: number, { rise, fall }: { rise: number; fall: number }) =>
  value + (target - value) * (1 - Math.exp(-seconds / (target > value ? rise : fall)))

/** Тембр ноты подложки: доли нижней октавы, звона и дребезга. */
interface Color {
  sub: number
  bell: number
  grit: number
}
const PLAIN: Color = { sub: 0, bell: 0, grit: 0 }

/** Плавное число от -1 до 1, которое медленно бродит: за единицу t — один шаг к новому случайному значению. */
function wander(seed: number) {
  const value = (step: number) => {
    const next = random(seed * 1000003 + step * 7919 + 1)
    next()
    return next() * 2 - 1
  }
  return (t: number) => {
    const step = Math.floor(t)
    const f = t - step
    const a = value(step)
    return a + (value(step + 1) - a) * f * f * (3 - 2 * f)
  }
}

export interface Ambience {
  /** Раз в кадр: фон подстраивается под погоду и высоту камеры. */
  update(seconds: number, mood: Mood): void
}

/**
 * Фон заброшенной пустой земли под низким небом. Всё время дует ветер, порывами; в развалинах то и дело гудит
 * и воет; в непогоду шуршат песок и дождь. Под этим — едва слышная медленная подложка в миноре, а вдалеке, сквозь
 * эхо, изредка скрипит ржавый металл, лязгает железо или, в бурю, рокочет гром.
 * Ветер крепнет вместе с погодой и с высотой камеры, подложка ночью темнее, ниже и реже.
 *
 * Подложка одна, а оттенки у неё — от обстановки. Противник в виду — под ней тихо бьётся пульс и проскальзывает
 * нота на полтона выше баса; в бою пульс чаще и громче, под ним гудит низкая пила, ноты гуще, вдалеке чаще лязг.
 * Успех — открыта тема, отбита атака — звучит мажорной фразой, и аккорды на время светлеют. Биом под камерой
 * красит ноты: эрг тёплый, с нижней октавой; солончаки звенят стеклом; красные пустоши дребезжат металлом; топи
 * глушат звук и тонут в эхе. Всё это меняется плавно: обстановку фон сглаживает сам (EASE), а звук догоняет её
 * ещё и через setTargetAtTime.
 */
export function createAmbience(
  context: BaseAudioContext,
  output: AudioNode,
  noises: Noises,
  impulse: AudioBuffer,
  distant: Map<DistantName, AudioBuffer[]>,
): Ambience {
  const next = random(Date.now() % 100000)
  const between = ([from, to]: number[]) => from + next() * (to - from)

  const bus = context.createGain()
  bus.gain.setValueAtTime(0, context.currentTime)
  bus.gain.linearRampToValueAtTime(1, context.currentTime + FADE_IN)
  bus.connect(output)
  // Эхо простора: туда уходят подложка, гулы и всё далёкое.
  const reverb = context.createConvolver()
  reverb.buffer = impulse
  const reverbGain = context.createGain()
  reverbGain.gain.value = REVERB_LEVEL
  reverb.connect(reverbGain).connect(bus)

  const filter = (type: BiquadFilterType, frequency: number, q = 0.7) => {
    const node = context.createBiquadFilter()
    node.type = type
    node.frequency.value = frequency
    node.Q.value = q
    return node
  }
  const gain = (value = 0) => {
    const node = context.createGain()
    node.gain.value = value
    return node
  }

  // Ветер — бурый шум, у которого порывами то открываются, то закрываются верха.
  const windFilter = filter('lowpass', 400)
  const windGain = gain()
  loop(context, noises.brown, 0).connect(windFilter).connect(windGain).connect(bus)

  // Гулы — розовый шум сквозь узкую полосу: ветер воет в пустых коробках зданий.
  const howls = HOWLS.map((howl, i) => {
    const band = filter('bandpass', howl.frequency, howl.q)
    const level = gain()
    const panner = context.createStereoPanner()
    loop(context, noises.pink, 2.1 + i * 3.3).connect(band).connect(level).connect(panner)
    panner.connect(bus)
    panner.connect(reverb)
    return { ...howl, band, level, panner }
  })

  // Шорох непогоды — белый шум без низов и самых верхов: песок по железу или дождь.
  const hissGain = gain()
  loop(context, noises.white, 0.7).connect(filter('highpass', 1500)).connect(filter('lowpass', 6500)).connect(hissGain).connect(bus)

  // Подложка: ноты всплывают и тают, сквозь мягкий фильтр, в основном через эхо.
  const padFilter = filter('lowpass', 900)
  const padGain = gain(PAD_LEVEL)
  padFilter.connect(padGain)
  // Сухо и в эхо — порознь: в топях звук тонет в эхе.
  const padDry = gain(1)
  const padWet = gain(1)
  padGain.connect(padDry).connect(bus)
  padGain.connect(padWet).connect(reverb)

  // Пульс — мимо фильтра подложки, сквозь свой глухой: удар слышен, но не стучит.
  const pulseFilter = filter('lowpass', 260)
  const pulseGain = gain(1)
  pulseFilter.connect(pulseGain).connect(bus)

  // Гул боя: две чуть разные пилы на басу аккорда, сквозь глухой фильтр. Звучат всегда, в тишине — на нуле.
  const droneFilter = filter('lowpass', 150, 1.2)
  const droneGain = gain()
  droneFilter.connect(droneGain)
  droneGain.connect(bus)
  droneGain.connect(reverb)
  const drones = [-7, 7].map((detune) => {
    const oscillator = context.createOscillator()
    oscillator.type = 'sawtooth'
    oscillator.frequency.value = midi(CHORDS[0][0])
    oscillator.detune.value = detune
    oscillator.connect(droneFilter)
    oscillator.start()
    return oscillator
  })

  /**
   * Нота подложки: всплывает за rise секунд, держится hold и тает за fade. color — окраска биома: нижняя октава,
   * звон (третья гармоника, тает быстрее самой ноты) и дребезг (чуть расстроенный квадрат).
   */
  const note = (frequency: number, at: number, rise: number, hold: number, fade: number, level: number, color: Color = PLAIN, output: AudioNode = padFilter) => {
    const end = at + rise + hold + fade
    const envelope = context.createGain()
    envelope.gain.setValueAtTime(0, at)
    envelope.gain.linearRampToValueAtTime(level, at + rise)
    envelope.gain.setValueAtTime(level, at + rise + hold)
    envelope.gain.linearRampToValueAtTime(0, end)
    envelope.connect(output)
    // Две чуть разные волны — нота живая и тёплая; окраска биома — ещё голоса поверх.
    const voices = [
      { type: 'sine' as OscillatorType, ratio: 1, detune: -4, level: 1 },
      { type: 'triangle' as OscillatorType, ratio: 1, detune: 5, level: 0.45 },
      { type: 'sine' as OscillatorType, ratio: 0.5, detune: 0, level: ERG_SUB * color.sub },
      { type: 'square' as OscillatorType, ratio: 1, detune: 13, level: RED_GRIT * color.grit },
    ].filter((voice) => voice.level > 0.01)
    const nodes: AudioNode[] = [envelope]
    const started = voices.map((voice) => {
      const oscillator = context.createOscillator()
      oscillator.type = voice.type
      oscillator.frequency.value = frequency * voice.ratio
      oscillator.detune.value = voice.detune
      const mix = gain(voice.level)
      oscillator.connect(mix).connect(envelope)
      oscillator.start(at)
      oscillator.stop(end + 0.1)
      nodes.push(oscillator, mix)
      return oscillator
    })
    // Звон — своя огибающая: вступает с нотой и тает раньше неё, как удар по стеклу.
    if (color.bell > 0.01) {
      const ring = context.createGain()
      ring.gain.setValueAtTime(0, at)
      ring.gain.linearRampToValueAtTime(level * SALT_BELL * color.bell, at + Math.min(rise, 0.4))
      ring.gain.exponentialRampToValueAtTime(0.0001, at + Math.min(rise, 0.4) + 3)
      const oscillator = context.createOscillator()
      oscillator.frequency.value = frequency * 3
      oscillator.connect(ring).connect(output)
      oscillator.start(at)
      oscillator.stop(at + Math.min(rise, 0.4) + 3.1)
      nodes.push(oscillator, ring)
      started.push(oscillator)
    }
    let left = started.length
    for (const oscillator of started) {
      oscillator.onended = () => {
        if (--left) return
        for (const node of nodes) node.disconnect()
      }
    }
  }

  /** Удар пульса: низкая нота, вступает сразу и тает за секунду. */
  const beat = (frequency: number, at: number, level: number) => {
    const envelope = context.createGain()
    envelope.gain.setValueAtTime(0, at)
    envelope.gain.linearRampToValueAtTime(level, at + 0.04)
    envelope.gain.exponentialRampToValueAtTime(0.0001, at + 0.9)
    envelope.connect(pulseFilter)
    const voices = [
      { type: 'sine' as OscillatorType, ratio: 1, level: 1 },
      // Октава выше — чтобы удар был слышен и в маленьких динамиках.
      { type: 'triangle' as OscillatorType, ratio: 2, level: 0.35 },
    ].map((voice) => {
      const oscillator = context.createOscillator()
      oscillator.type = voice.type
      oscillator.frequency.value = frequency * voice.ratio
      const mix = gain(voice.level)
      oscillator.connect(mix).connect(envelope)
      oscillator.start(at)
      oscillator.stop(at + 1)
      return { oscillator, mix }
    })
    voices[0].oscillator.onended = () => {
      for (const { oscillator, mix } of voices) {
        oscillator.disconnect()
        mix.disconnect()
      }
      envelope.disconnect()
    }
  }

  /** Что-то далеко: звук name сквозь даль — глухо, в стороне и больше эхом, чем напрямую. */
  const far = (name: DistantName, level: number) => {
    const variants = distant.get(name)!
    const source = context.createBufferSource()
    source.buffer = variants[Math.floor(next() * variants.length)]
    source.playbackRate.value = 0.85 + next() * 0.3
    const muffle = filter('lowpass', name === 'thunder' ? 400 : 1200 + next() * 1500)
    const panner = context.createStereoPanner()
    panner.pan.value = (next() * 2 - 1) * 0.8
    const dry = gain(level * (name === 'thunder' ? 0.8 : 0.3))
    const wet = gain(level)
    source.connect(muffle).connect(panner)
    panner.connect(dry).connect(bus)
    panner.connect(wet).connect(reverb)
    source.onended = () => {
      for (const node of [source, muffle, panner, dry, wet]) node.disconnect()
    }
    source.start()
  }

  const gust = [wander(1), wander(2)]
  const howlSwell = HOWLS.map((_, i) => wander(10 + i))
  const howlPitch = HOWLS.map((_, i) => wander(20 + i))
  const howlPan = HOWLS.map((_, i) => wander(30 + i))
  let time = 0
  let sinceStep = STEP
  let chord = 0
  let chordAt = context.currentTime
  let noteAt = context.currentTime + 1
  let distantAt = context.currentTime + between(DISTANT_GAP)
  let pulseAt = context.currentTime
  /** Обстановка, сглаженная: ей следует звук. */
  let threat = 0
  let battle = 0
  const biomes = [0.25, 0.25, 0.25, 0.25]
  /** Счёт успехов, уже отыгранных; undefined — ещё не видели: успехи до начала игры не звучат. */
  let triumphs: number | undefined
  /** До какого времени аккорды светлые и ждёт ли своей очереди мажорная фраза. */
  let brightUntil = -Infinity
  let fanfare = false
  // Бас первого аккорда.
  note(midi(CHORDS[0][0]), context.currentTime + 0.5, 6, CHORD_TIME - 6, 8, 0.8)

  return {
    update(seconds, mood) {
      time += seconds
      sinceStep += seconds
      if (sinceStep < STEP) return
      // Сглаживание — за всё время с прошлого шага: кадры бывают и редкими.
      const elapsed = sinceStep
      sinceStep = 0
      const now = context.currentTime
      const wind = clamp01(mood.wind)
      const precipitation = clamp01(mood.precipitation)
      const light = clamp01(mood.light)
      const altitude = clamp01(mood.altitude)
      threat = ease(threat, clamp01(mood.threat), elapsed, EASE.threat)
      battle = ease(battle, clamp01(mood.battle), elapsed, EASE.battle)
      biomes.forEach((share, i) => (biomes[i] = ease(share, clamp01(mood.biomes[i] ?? 0), elapsed, EASE.biome)))
      const [erg, salt, red, marsh] = biomes
      const color: Color = { sub: erg, bell: salt, grit: red }
      if (triumphs === undefined) triumphs = mood.triumphs
      else if (mood.triumphs > triumphs) {
        triumphs = mood.triumphs
        brightUntil = now + TRIUMPH_SECONDS
        fanfare = true
      }

      // Ветер дует всегда: и в штиль на пустой земле что-то шелестит. Порыв — два медленных колебания разом.
      const strength = 0.3 + 0.7 * Math.max(wind, precipitation)
      const gusting = clamp01(0.5 + 0.35 * gust[0](time / 7) + 0.25 * gust[1](time / 2.3))
      const blow = WIND_LEVEL * (0.4 + 0.6 * strength) * (0.6 + 0.8 * gusting) * (0.75 + 0.5 * altitude)
      windGain.gain.setTargetAtTime(blow, now, 0.3)
      windFilter.frequency.setTargetAtTime(200 + (500 + 400 * altitude) * strength * (0.5 + gusting), now, 0.3)

      // Гулы накатывают и уходят; в сильный ветер чаще и громче, у земли слышнее, чем с высоты.
      howls.forEach((howl, i) => {
        const swell = Math.pow(clamp01(howlSwell[i](time / 9) + 0.15 * strength), 1.5)
        const level = HOWL_LEVELS[i] * swell * (0.25 + 0.75 * strength) * (1 - 0.4 * altitude)
        howl.level.gain.setTargetAtTime(level, now, 0.5)
        howl.band.frequency.setTargetAtTime(howl.frequency * (1 + howl.drift * howlPitch[i](time / 5) + 0.15 * gusting), now, 0.5)
        howl.panner.pan.setTargetAtTime(0.7 * howlPan[i](time / 13), now, 0.5)
      })

      hissGain.gain.setTargetAtTime(HISS_LEVEL * Math.pow(precipitation, 1.2) * (0.7 + 0.6 * gusting), now, 0.4)

      // Подложка: днём светлее, ночью темнее и чуть громче; в бурю отступает за ветер. Солончаки звонче, топи
      // глуше и тонут в эхе; в тревоге и бою подложка ближе.
      const open = (1 + 0.5 * salt) * (1 - 0.45 * marsh) * (1 + 0.3 * battle)
      padFilter.frequency.setTargetAtTime((450 + 900 * light * (1 - 0.5 * precipitation)) * open, now, 1)
      padGain.gain.setTargetAtTime(PAD_LEVEL * (1 + 0.4 * (1 - light)) * (1 - 0.5 * precipitation) * (1 + 0.3 * threat + 0.25 * battle), now, 1)
      padDry.gain.setTargetAtTime(1 - 0.5 * marsh, now, 1)
      padWet.gain.setTargetAtTime(1 + 0.6 * marsh, now, 1)

      // Гул боя: на басу нынешнего аккорда, громче и светлее, чем жарче бой.
      droneGain.gain.setTargetAtTime(DRONE_LEVEL * Math.pow(battle, 1.5), now, 1)
      droneFilter.frequency.setTargetAtTime(150 + 350 * battle, now, 1)

      // Ноты подложки планируются чуть вперёд: точное время — у звука, не у кадров.
      const ahead = now + 0.5
      if (ahead >= chordAt + CHORD_TIME) {
        chordAt += CHORD_TIME
        // Вкладка спала — аккорды не догоняют упущенное.
        if (chordAt + CHORD_TIME < now) chordAt = now
        chord = (chord + 1) % CHORDS.length
        note(midi(CHORDS[chord][0]), Math.max(now, chordAt), 6, CHORD_TIME - 6, 8, 0.8, { ...color, bell: 0 })
        for (const drone of drones) drone.frequency.setTargetAtTime(midi(CHORDS[chord][0]), Math.max(now, chordAt), 2)
      }
      const bass = CHORDS[chord][0]
      if (ahead >= noteAt) {
        noteAt = Math.max(noteAt, now)
        if (fanfare) {
          // Успех: мажорное трезвучие от баса аккорда, нота за нотой вверх, звонко.
          fanfare = false
          ;[24, 28, 31, 36].forEach((step, i) => note(midi(bass + step), noteAt + i * 0.7, 0.8, 1 + (3 - i) * 0.5, 5, 0.55, { ...color, bell: Math.max(0.6, salt) }))
          noteAt += 4
        } else {
          // Светлые аккорды после успеха: малая терция над басом становится большой.
          const notes = CHORDS[chord].slice(1).map((pitch) => (now < brightUntil && (pitch - bass) % 12 === 3 ? pitch + 1 : pitch))
          let pitch = notes[Math.floor(next() * notes.length)]
          // Тревога: иногда — нота на полтона выше баса, она режет минор.
          if (next() < 0.35 * Math.max(threat, battle)) pitch = bass + 25
          // Ночью ноты чаще уходят октавой ниже.
          else if (next() < 0.4 * (1 - light)) pitch -= 12
          // В бою ноты короче: всплывают быстрее и не держатся.
          const urgent = 1 - 0.6 * battle
          note(midi(pitch), noteAt, between([2.5, 5]) * urgent, between([1, 4]) * urgent, between([4, 7]) * (1 - 0.4 * battle), 0.35 + next() * 0.3, color)
          // Ночью реже, в тревоге и бою — гуще.
          noteAt += between(NOTE_GAP) * (1 + 0.3 * (1 - light)) * (1 - 0.2 * threat) * (1 - 0.45 * battle)
        }
      }

      // Пульс: слышен, пока есть тревога или бой; в бою чаще. Тишина — следующий удар ждёт с этого мига.
      const pulse = Math.max(0.6 * threat, battle)
      if (pulse < 0.03) pulseAt = Math.max(pulseAt, now)
      else if (ahead >= pulseAt) {
        pulseAt = Math.max(pulseAt, now)
        const level = PULSE_LEVEL * pulse
        beat(midi(bass), pulseAt, level)
        beat(midi(bass), pulseAt + PULSE_ECHO, level * PULSE_ECHO_LEVEL)
        pulseAt += PULSE_BEAT[0] + (PULSE_BEAT[1] - PULSE_BEAT[0]) * battle
      }

      // Вдалеке: в бурю — чаще гром, иначе скрип или лязг. С высоты земля дальше и тише.
      // В бою и в красных пустошах вдалеке чаще лязгает железо, а в бою — и чаще вообще.
      if (now >= distantAt) {
        const name: DistantName = next() < precipitation * 0.8 ? 'thunder' : next() < 0.55 - 0.35 * Math.max(battle, 0.6 * red) ? 'creak' : 'clank'
        far(name, DISTANT_LEVEL * (0.5 + 0.5 * next()) * (1 - 0.5 * altitude) * (name === 'thunder' ? 0.7 + precipitation : 1))
        distantAt = now + between(DISTANT_GAP) * (1 - 0.4 * precipitation) * (1 - 0.5 * battle)
      }
    },
  }
}
