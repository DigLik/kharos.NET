/** Чем звучит мотор в этот миг: всё от 0, обороты и ход — скорость петель. */
export interface MotorSound {
  /** Скорость петли мотора: 1 — холостые, больше — обороты выше. */
  rate: number
  /** Нагрузка: 0 — холостой ход, 1 — газ в пол. */
  load: number
  /** Громкость мотора: 0 — заглох. */
  level: number
  /** Громкость хода — шин или гусениц — и скорость его петли. */
  running: number
  runningRate: number
  /** Громкость скрипа разворота на месте. */
  squeal: number
  /** Общая громкость голоса — по тому, насколько машина близко. */
  volume: number
  /** Сторона: -1 — слева, 1 — справа. */
  pan: number
  /** Насколько глухо: 1 — рядом, 0 — далеко, верхи тонут. */
  clarity: number
}

export interface MotorVoice {
  set(sound: MotorSound): void
  /** Голос стихает и освобождается: машину больше не слышно отдельно. */
  release(): void
}

/** Петли мотора: холостая и под нагрузкой, в одном такте. */
export interface MotorBuffers {
  idle: AudioBuffer
  load: AudioBuffer
}

/** Как быстро голос догоняет новые обороты и громкость, в секундах: так переход плавный, но не вялый. */
const GLIDE = 0.06
/** За сколько секунд стихает отпущенный голос. */
const RELEASE = 0.25
/** Громкость хода и скрипа рядом с мотором. */
const RUNNING_LEVEL = 0.55
const SQUEAL_LEVEL = 0.35

/**
 * Голос одной машины: две петли мотора крутятся с одной скоростью и одного места, поэтому их такты совпадают, и голос
 * плавно перетекает от холостой к нагруженной, пока водитель жмёт на газ. Под ними — ход (шины или гусеницы, их
 * петля крутится вслед за скоростью) и скрип разворота. Всё через срез верхов: на газу и вблизи звук ярче.
 */
export function createMotorVoice(
  context: BaseAudioContext,
  output: AudioNode,
  motor: MotorBuffers,
  running: AudioBuffer | null,
  squeal: AudioBuffer | null,
): MotorVoice {
  const now = context.currentTime
  const out = context.createGain()
  out.gain.value = 0
  const panner = context.createStereoPanner()
  out.connect(panner).connect(output)

  const tone = context.createBiquadFilter()
  tone.type = 'lowpass'
  tone.Q.value = 0.7
  const engine = context.createGain()
  engine.gain.value = 0
  tone.connect(engine).connect(out)

  const offset = Math.random() * motor.idle.duration
  const layer = (buffer: AudioBuffer) => {
    const source = context.createBufferSource()
    source.buffer = buffer
    source.loop = true
    const gain = context.createGain()
    gain.gain.value = 0
    source.connect(gain)
    source.start(now, offset % buffer.duration)
    return { source, gain }
  }
  const idle = layer(motor.idle)
  const load = layer(motor.load)
  idle.gain.connect(tone)
  load.gain.connect(tone)

  const aux = (buffer: AudioBuffer | null) => {
    if (!buffer) return null
    const source = context.createBufferSource()
    source.buffer = buffer
    source.loop = true
    const gain = context.createGain()
    gain.gain.value = 0
    source.connect(gain).connect(out)
    source.start(now, Math.random() * buffer.duration)
    return { source, gain }
  }
  const ground = aux(running)
  const scrape = aux(squeal)
  const sources = [idle.source, load.source, ground?.source, scrape?.source].filter((source) => !!source)

  return {
    set(sound) {
      const at = context.currentTime
      for (const source of [idle.source, load.source]) source.playbackRate.setTargetAtTime(Math.max(0.1, sound.rate), at, GLIDE)
      // Переход холостая — нагрузка по четверти круга: суммарная громкость не проседает посередине.
      const angle = (Math.max(0, Math.min(1, sound.load)) * Math.PI) / 2
      idle.gain.gain.setTargetAtTime(Math.cos(angle), at, GLIDE)
      load.gain.gain.setTargetAtTime(Math.sin(angle) * 1.2, at, GLIDE)
      engine.gain.setTargetAtTime(sound.level, at, GLIDE * 2)
      // На газу и на высоких оборотах звук светлее; издалека верхи тонут.
      const bright = (500 + 3500 * sound.load) * (0.7 + 0.3 * sound.rate)
      tone.frequency.setTargetAtTime(Math.min(16000, bright * (0.35 + 0.65 * sound.clarity)), at, GLIDE)
      if (ground) {
        ground.source.playbackRate.setTargetAtTime(Math.max(0.05, sound.runningRate), at, GLIDE)
        ground.gain.gain.setTargetAtTime(sound.running * RUNNING_LEVEL, at, GLIDE * 2)
      }
      if (scrape) scrape.gain.gain.setTargetAtTime(sound.squeal * SQUEAL_LEVEL, at, GLIDE * 2)
      out.gain.setTargetAtTime(sound.volume, at, GLIDE * 2)
      panner.pan.setTargetAtTime(Math.max(-1, Math.min(1, sound.pan)), at, GLIDE * 2)
    },
    release() {
      const at = context.currentTime
      out.gain.cancelScheduledValues(at)
      out.gain.setTargetAtTime(0, at, RELEASE / 3)
      for (const source of sources) {
        source.stop(at + RELEASE * 2)
        source.onended = () => source.disconnect()
      }
      setTimeout(() => panner.disconnect(), (RELEASE * 2 + 0.1) * 1000)
    },
  }
}
