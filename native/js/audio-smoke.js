// Проверка звука нативной игры — запускается вместо собранной игры: `KHAROS_SCRIPT=js/audio-smoke.js cargo run`.
// Граф — тот же, что заводит игра (`src/audio`): компрессор на выходе, группы громкости, петли, свёртка эха, фильтры,
// осцилляторы и разовые звуки с `onended`. Слышно короткий тон и щелчки; итог каждой сверки — в журнале.

const RATE = 48000
const context = new AudioContext({ sampleRate: RATE })
const results = []
const check = (name, ok, detail = '') => {
  results.push(ok)
  console.log(`${ok ? 'сходится' : 'НЕ сходится'}: ${name}${detail ? ` (${detail})` : ''}`)
}

check('контекст играет', context.state === 'running', context.state)
check('частота контекста', context.sampleRate === RATE, String(context.sampleRate))

/** Запись: щелчок — затухающий шум, length отсчётов. */
const click = (length, channels = 1) => {
  const buffer = new AudioBuffer({ numberOfChannels: channels, length, sampleRate: RATE })
  for (let channel = 0; channel < channels; channel++) {
    const data = new Float32Array(length)
    for (let i = 0; i < length; i++) data[i] = (Math.random() * 2 - 1) * Math.exp(-i / (length / 6)) * 0.3
    buffer.copyToChannel(data, channel)
  }
  return buffer
}

// Выход — как у игры: компрессор, общая громкость, группа.
const compressor = context.createDynamicsCompressor()
compressor.threshold.value = -18
compressor.ratio.value = 6
compressor.connect(context.destination)
const master = context.createGain()
master.gain.value = 0.6
master.connect(compressor)
check('значение параметра', Math.abs(master.gain.value - 0.6) < 1e-6, String(master.gain.value))
const bus = context.createGain()
bus.connect(master)

// Эхо — свёртка стерео-записью на частоте контекста.
const reverb = context.createConvolver()
reverb.buffer = click(RATE, 2)
const wet = context.createGain()
wet.gain.value = 0.3
reverb.connect(wet).connect(master)

// Петля через фильтр и панораму; её громкость плавно уходит к цели.
const loop = context.createBufferSource()
loop.buffer = click(RATE / 2)
loop.loop = true
const loopGain = context.createGain()
loopGain.gain.value = 0
const panner = context.createStereoPanner()
const filter = context.createBiquadFilter()
filter.type = 'lowpass'
filter.frequency.value = 1200
filter.Q.value = 1.5
loop.connect(filter).connect(loopGain).connect(panner).connect(bus)
loop.start(0, 0.1)
loopGain.gain.setTargetAtTime(0.4, context.currentTime, 0.1)
panner.pan.setTargetAtTime(-0.5, context.currentTime, 0.1)

// Тон: пила с огибающей и дрожью частоты от второго осциллятора — соединение узла с параметром.
const tone = context.createOscillator()
tone.type = 'sawtooth'
tone.frequency.value = 220
const vibrato = context.createOscillator()
vibrato.frequency.value = 5
const depth = context.createGain()
depth.gain.value = 6
vibrato.connect(depth).connect(tone.frequency)
const envelope = context.createGain()
const at = context.currentTime + 0.05
envelope.gain.setValueAtTime(0, at)
envelope.gain.linearRampToValueAtTime(0.15, at + 0.05)
envelope.gain.setValueAtTime(0.15, at + 0.4)
envelope.gain.exponentialRampToValueAtTime(0.0001, at + 0.8)
tone.connect(envelope).connect(bus)
envelope.connect(reverb)
let toneEnded = false
tone.onended = () => {
  toneEnded = true
  tone.disconnect()
  envelope.disconnect()
}
tone.start(at)
tone.stop(at + 0.9)
vibrato.start(at)
vibrato.stop(at + 0.9)

let threw = false
try {
  tone.start()
} catch (error) {
  threw = true
  console.log(`второй start: ${error.message}`)
}
check('второй start бросает исключение, а не роняет окно', threw)

// Разовые звуки, как у игры: каждые 60 мс — щелчок со своим голосом, отпускаются по onended.
const shot = click(RATE / 20)
let played = 0
let ended = 0
const timer = setInterval(() => {
  if (played >= 30) return clearInterval(timer)
  const source = context.createBufferSource()
  source.buffer = shot
  source.playbackRate.value = 0.9 + Math.random() * 0.2
  const gain = context.createGain()
  gain.gain.value = 0.5
  const side = context.createStereoPanner()
  side.pan.value = Math.random() * 2 - 1
  source.connect(gain).connect(side).connect(bus)
  source.onended = () => {
    ended++
    source.disconnect()
    gain.disconnect()
    side.disconnect()
  }
  source.start()
  played++
}, 60)

// Тысяча голосов, которые игра бросает, не дослушав: номера уходят сборщику мусора, окно не должно упасть.
for (let i = 0; i < 1000; i++) {
  const source = context.createBufferSource()
  source.buffer = shot
  const gain = context.createGain()
  gain.gain.value = 0
  source.connect(gain).connect(bus)
  source.start()
}

const startedAt = context.currentTime
setTimeout(() => {
  const passed = context.currentTime - startedAt
  check('время контекста идёт', passed > 2 && passed < 3.5, `${passed.toFixed(2)} с за 2,5 с`)
  check('громкость петли дошла до цели', Math.abs(loopGain.gain.value - 0.4) < 0.01, loopGain.gain.value.toFixed(3))
  check('панорама дошла до цели', Math.abs(panner.pan.value + 0.5) < 0.01, panner.pan.value.toFixed(3))
  check('тон доиграл (onended)', toneEnded)
  check('разовые звуки доиграли', played === 30 && ended === 30, `${ended} из ${played}`)
  void context.suspend()
  check('приостановка', context.state === 'suspended', context.state)
  void context.resume()
  check('продолжение', context.state === 'running', context.state)
  void context.close()
  check('закрытие', context.state === 'closed', context.state)
  // После закрытия узлы молчат, но вызовы не падают.
  loopGain.gain.setTargetAtTime(0, 0, 0.1)
  console.log(`звук: ${results.filter(Boolean).length} из ${results.length} сверок сходится`)
}, 2500)
