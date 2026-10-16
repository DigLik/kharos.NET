// Звук для нативной игры — Web Audio API в том объёме, что берёт `packages/client/src/audio`: контекст, узлы, их параметры с
// расписанием и записи. Сам граф и вывод на устройство — в Rust (`packages/client/src/audio.rs`, крейт `web-audio-api`), а здесь
// классы в форме браузерных: игра заводит звук так же, как на странице.
//
// Узлы и записи Rust держит по номерам. Когда игра отпускает объект, его номер отпускается и в Rust
// (`FinalizationRegistry`): граф крейта сам держит узел, пока тот звучит. Источник с `onended` держится здесь, пока
// не доиграет: конец звучания приходит из Rust раз в кадр.
//
// В рантайм файл идёт обычным скриптом после `prelude.js` (см. `engine.rs`).
(() => {
  const ops = globalThis.Deno.core.ops

  /** Номера видов узлов и имён параметров — те же, что у Rust (`Node` и `Node::param` в `packages/client/src/audio.rs`). */
  const KIND = { destination: 0, gain: 1, oscillator: 2, source: 3, biquad: 4, panner: 5, convolver: 6, compressor: 7 }
  const PARAM = { gain: 0, frequency: 1, detune: 2, Q: 3, playbackRate: 4, pan: 5, threshold: 6, ratio: 7, knee: 8, attack: 9, release: 10 }
  /** Виды фильтров и волн по порядку — тот же порядок у Rust (`op_audio_set`). */
  const FILTERS = ['lowpass', 'highpass', 'bandpass', 'lowshelf', 'highshelf', 'peaking', 'notch', 'allpass']
  const WAVES = ['sine', 'square', 'sawtooth', 'triangle']
  const STATES = ['suspended', 'running', 'closed']
  /** Вызовы расписания по номерам — для текста ошибки. */
  const METHODS = ['value', 'setValueAtTime', 'linearRampToValueAtTime', 'exponentialRampToValueAtTime', 'setTargetAtTime', 'cancelScheduledValues', 'cancelAndHoldAtTime']

  const nodes = new FinalizationRegistry((id) => ops.op_audio_drop(id))
  const buffers = new FinalizationRegistry((id) => ops.op_audio_buffer_drop(id))
  /** Источники, которые ждут своего `onended`, по номерам. */
  const waiting = new Map()

  /** Байты типизированного массива без копирования: Rust читает ту же память, что и игра. */
  const bytes = (data) => new Uint8Array(data.buffer, data.byteOffset, data.byteLength)

  /** Конец звучания источников — раз в кадр, до работы кадра: так `onended` приходит, как у страницы, задачей. */
  const frame = globalThis.__kharos_frame
  globalThis.__kharos_frame = (milliseconds) => {
    for (const id of ops.op_audio_ended()) {
      const source = waiting.get(id)
      waiting.delete(id)
      source?.onended?.call(source, { type: 'ended', target: source })
    }
    frame(milliseconds)
  }

  /** Параметр узла с расписанием. Держит свой узел: пока жив параметр, жив и тот, кому он принадлежит. */
  class AudioParam {
    #code
    #name
    constructor(node, name, value) {
      this.node = node
      this.#name = name
      this.#code = PARAM[name]
      this.defaultValue = value
    }

    /** Вызов расписания. Ошибка называет узел, параметр и числа: так видно, кто прислал не число. */
    #set(method, value, time = 0, extra = 0) {
      try {
        ops.op_audio_param(this.node.id, this.#code, method, value, time, extra)
      } catch (error) {
        error.message = `${this.node.constructor.name}.${this.#name} (${METHODS[method]}: ${value}, ${time}, ${extra}): ${error.message}`
        throw error
      }
      return this
    }

    get value() {
      return ops.op_audio_param_value(this.node.id, this.#code)
    }

    set value(value) {
      this.#set(0, value)
    }

    setValueAtTime(value, time) {
      return this.#set(1, value, time)
    }

    linearRampToValueAtTime(value, time) {
      return this.#set(2, value, time)
    }

    exponentialRampToValueAtTime(value, time) {
      return this.#set(3, value, time)
    }

    setTargetAtTime(value, time, constant) {
      return this.#set(4, value, time, constant)
    }

    cancelScheduledValues(time) {
      return this.#set(5, time)
    }

    cancelAndHoldAtTime(time) {
      return this.#set(6, time)
    }

    /** Номер имени параметра — для соединения узла с ним. */
    get code() {
      return this.#code
    }
  }

  /** Параметры узла — свойствами с именами браузера: values — значения по умолчанию. */
  const params = (node, values) => {
    for (const [name, value] of Object.entries(values)) node[name] = new AudioParam(node, name, value)
  }

  class AudioNode {
    constructor(context, kind) {
      this.context = context
      this.id = ops.op_audio_node(context.id, KIND[kind])
      if (this.id) nodes.register(this, this.id)
    }

    connect(target, output = 0, input = 0) {
      if (target instanceof AudioParam) ops.op_audio_connect(this.id, target.node.id, target.code, output, 0)
      else ops.op_audio_connect(this.id, target.id, -1, output, input)
      return target
    }

    disconnect(target) {
      if (!target) ops.op_audio_disconnect(this.id, 0, -1)
      else if (target instanceof AudioParam) ops.op_audio_disconnect(this.id, target.node.id, target.code)
      else ops.op_audio_disconnect(this.id, target.id, -1)
    }
  }

  class AudioDestinationNode extends AudioNode {
    constructor(context) {
      super(context, 'destination')
    }
  }

  class GainNode extends AudioNode {
    constructor(context) {
      super(context, 'gain')
      params(this, { gain: 1 })
    }
  }

  class StereoPannerNode extends AudioNode {
    constructor(context) {
      super(context, 'panner')
      params(this, { pan: 0 })
    }
  }

  class BiquadFilterNode extends AudioNode {
    #type = 'lowpass'
    constructor(context) {
      super(context, 'biquad')
      params(this, { frequency: 350, detune: 0, Q: 1, gain: 0 })
    }

    get type() {
      return this.#type
    }

    set type(value) {
      const index = FILTERS.indexOf(value)
      if (index < 0) return
      this.#type = value
      ops.op_audio_set(this.id, 0, index)
    }
  }

  class DynamicsCompressorNode extends AudioNode {
    constructor(context) {
      super(context, 'compressor')
      params(this, { threshold: -24, knee: 30, ratio: 12, attack: 0.003, release: 0.25 })
      this.reduction = 0
    }
  }

  class ConvolverNode extends AudioNode {
    #buffer = null
    #normalize = true
    constructor(context) {
      super(context, 'convolver')
    }

    get buffer() {
      return this.#buffer
    }

    set buffer(value) {
      this.#buffer = value
      if (value) ops.op_audio_set_buffer(this.id, value.id)
    }

    get normalize() {
      return this.#normalize
    }

    set normalize(value) {
      this.#normalize = !!value
      ops.op_audio_set(this.id, 4, value ? 1 : 0)
    }
  }

  /** Источник звука: запуск, остановка и конец звучания. */
  class AudioScheduledSourceNode extends AudioNode {
    #onended = null

    start(when = 0, offset = 0, duration) {
      ops.op_audio_start(this.id, when, offset, duration ?? -1)
    }

    stop(when = 0) {
      ops.op_audio_stop(this.id, when)
    }

    get onended() {
      return this.#onended
    }

    set onended(handler) {
      this.#onended = handler
      if (handler) waiting.set(this.id, this)
      else waiting.delete(this.id)
    }
  }

  class OscillatorNode extends AudioScheduledSourceNode {
    #type = 'sine'
    constructor(context) {
      super(context, 'oscillator')
      params(this, { frequency: 440, detune: 0 })
    }

    get type() {
      return this.#type
    }

    set type(value) {
      const index = WAVES.indexOf(value)
      if (index < 0) return
      this.#type = value
      ops.op_audio_set(this.id, 0, index)
    }
  }

  class AudioBufferSourceNode extends AudioScheduledSourceNode {
    #buffer = null
    #loop = false
    #loopStart = 0
    #loopEnd = 0
    constructor(context) {
      super(context, 'source')
      params(this, { playbackRate: 1, detune: 0 })
    }

    get buffer() {
      return this.#buffer
    }

    set buffer(value) {
      this.#buffer = value
      if (value) ops.op_audio_set_buffer(this.id, value.id)
    }

    get loop() {
      return this.#loop
    }

    set loop(value) {
      this.#loop = !!value
      ops.op_audio_set(this.id, 1, value ? 1 : 0)
    }

    get loopStart() {
      return this.#loopStart
    }

    set loopStart(value) {
      this.#loopStart = value
      ops.op_audio_set(this.id, 2, value)
    }

    get loopEnd() {
      return this.#loopEnd
    }

    set loopEnd(value) {
      this.#loopEnd = value
      ops.op_audio_set(this.id, 3, value)
    }
  }

  /** Запись звука. Как и у страницы, не привязана к контексту: одна годится любому. */
  class AudioBuffer {
    constructor({ numberOfChannels = 1, length, sampleRate }) {
      this.id = ops.op_audio_buffer(numberOfChannels, length, sampleRate)
      buffers.register(this, this.id)
      this.numberOfChannels = numberOfChannels
      this.length = length
      this.sampleRate = sampleRate
      this.duration = length / sampleRate
    }

    copyToChannel(source, channel, offset = 0) {
      ops.op_audio_buffer_write(this.id, bytes(source), channel, offset)
    }
  }

  /**
   * Контекст звука. Если устройства нет, контекст всё же заводится — закрытым: узлы у него пустые, а игра, видя
   * `state` не `running`, звук не играет (как на странице, где браузер звук не пустил).
   */
  class AudioContext {
    #destination = null
    /** Частота, если устройства нет: та, что просили, — по ней игра синтезирует эхо. */
    #rate
    constructor(options = {}) {
      this.id = ops.op_audio_context(options.sampleRate ?? 0)
      this.#rate = options.sampleRate ?? 48000
    }

    get currentTime() {
      return ops.op_audio_time(this.id)
    }

    get sampleRate() {
      return this.id ? ops.op_audio_rate(this.id) : this.#rate
    }

    get state() {
      return STATES[ops.op_audio_state(this.id)]
    }

    get destination() {
      return (this.#destination ??= new AudioDestinationNode(this))
    }

    resume() {
      ops.op_audio_control(this.id, 0)
      return Promise.resolve()
    }

    suspend() {
      ops.op_audio_control(this.id, 1)
      return Promise.resolve()
    }

    close() {
      ops.op_audio_control(this.id, 2)
      return Promise.resolve()
    }

    createGain() {
      return new GainNode(this)
    }

    createOscillator() {
      return new OscillatorNode(this)
    }

    createBufferSource() {
      return new AudioBufferSourceNode(this)
    }

    createBiquadFilter() {
      return new BiquadFilterNode(this)
    }

    createStereoPanner() {
      return new StereoPannerNode(this)
    }

    createConvolver() {
      return new ConvolverNode(this)
    }

    createDynamicsCompressor() {
      return new DynamicsCompressorNode(this)
    }

    createBuffer(numberOfChannels, length, sampleRate) {
      return new AudioBuffer({ numberOfChannels, length, sampleRate })
    }
  }

  Object.assign(globalThis, {
    AudioContext,
    BaseAudioContext: AudioContext,
    AudioBuffer,
    AudioParam,
    AudioNode,
    AudioDestinationNode,
    AudioScheduledSourceNode,
    GainNode,
    StereoPannerNode,
    BiquadFilterNode,
    DynamicsCompressorNode,
    ConvolverNode,
    OscillatorNode,
    AudioBufferSourceNode,
  })
})()
