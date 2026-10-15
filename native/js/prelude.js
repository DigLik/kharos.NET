// Замена браузера для нативной игры: игра должна работать как есть, поэтому здесь лежит ровно тот API,
// которого она просит. Состояние WebGL держит эта обёртка (как это делает браузер), а Rust трогают только
// на смене состояния и на отрисовке. Номера констант OpenGL — настоящие: те же, что игра читает из браузера.
//
// В рантайм файл идёт обычным скриптом (см. `engine.rs`), а не кодом расширения: от кода расширений deno_core
// требует семибитного ASCII, а комментарии и строки здесь русские. Обёртка ниже держит свои имена при себе —
// иначе игра столкнулась бы с ними: у неё есть и `canvas`, и `gl`.
(() => {
  const core = globalThis.Deno.core
  const ops = globalThis.__kharos_gl_ops

  /** Общая операция Rust: числа и связи. Восьмой аргумент — запас на будущее, сейчас всегда ноль. */
  const call = (op, a = 0, b = 0, c = 0, d = 0, e = 0, f = 0) => core.ops.op_gl(op, a, b, c, d, e, f)

  /** Байты типизированного массива без копирования: Rust читает ту же память, что и игра. */
  const bytes = (data) => new Uint8Array(data.buffer, data.byteOffset, data.byteLength)

  /** Константы OpenGL: их значения те же, что в WebGL 2. */
  const GL = {
    ZERO: 0, ONE: 1,
    TRIANGLES: 0x0004, TRIANGLE_STRIP: 0x0005,
    DEPTH_TEST: 0x0B71, CULL_FACE: 0x0B44, BLEND: 0x0BE2,
    SCISSOR_TEST: 0x0C11, UNPACK_ALIGNMENT: 0x0CF5, PACK_ALIGNMENT: 0x0D05,
    TEXTURE_2D: 0x0DE1,
    COLOR_BUFFER_BIT: 0x4000, DEPTH_BUFFER_BIT: 0x0100,
    UNSIGNED_BYTE: 0x1401, UNSIGNED_INT: 0x1405, UNSIGNED_SHORT: 0x1403,
    INT: 0x1404, FLOAT: 0x1406,
    RGBA: 0x1908, RGBA8: 0x8058,
    NEAREST: 0x2600, LINEAR: 0x2601,
    TEXTURE_MAG_FILTER: 0x2800, TEXTURE_MIN_FILTER: 0x2801,
    TEXTURE_WRAP_S: 0x2802, TEXTURE_WRAP_T: 0x2803,
    CLAMP_TO_EDGE: 0x812F, REPEAT: 0x2901,
    SRC_ALPHA: 0x0302, ONE_MINUS_SRC_ALPHA: 0x0303, DST_COLOR: 0x0306,
    ARRAY_BUFFER: 0x8892, ELEMENT_ARRAY_BUFFER: 0x8893,
    STATIC_DRAW: 0x88E4, DYNAMIC_DRAW: 0x88E8,
    FRAGMENT_SHADER: 0x8B30, VERTEX_SHADER: 0x8B31,
    COMPILE_STATUS: 0x8B81, LINK_STATUS: 0x8B82,
    ACTIVE_UNIFORMS: 0x8B86, ACTIVE_ATTRIBUTES: 0x8B89,
    FLOAT_VEC2: 0x8B50, FLOAT_VEC3: 0x8B51, FLOAT_VEC4: 0x8B52,
    INT_VEC2: 0x8B53, INT_VEC3: 0x8B54, INT_VEC4: 0x8B55,
    BOOL: 0x8B56, SAMPLER_2D: 0x8B5E,
    FRAMEBUFFER: 0x8D40, COLOR_ATTACHMENT0: 0x8CE0, FRAMEBUFFER_COMPLETE: 0x8CD5,
    TEXTURE0: 0x84C0,
    SYNC_GPU_COMMANDS_COMPLETE: 0x9117, ALREADY_SIGNALED: 0x9112,
    TIMEOUT_EXPIRED: 0x9111, CONDITION_SATISFIED: 0x911A, WAIT_FAILED: 0x911D,
  }

  /** Очередь кадров: как в браузере, колбэк получает время в миллисекундах. */
  const pending = new Map()
  let nextHandle = 1

  globalThis.requestAnimationFrame = (callback) => {
    const handle = nextHandle++
    pending.set(handle, callback)
    return handle
  }
  globalThis.cancelAnimationFrame = (handle) => pending.delete(handle)

  /** Что окно сказало до начала игры (см. `game_options` в `src/main.rs`): ровные ли часы, сценка, час суток. */
  const options = globalThis.__kharos_options ?? {}
  /** Время последнего кадра: при записи оно и есть часы игры. */
  let clock = 0

  /**
   * Кадр окна: Rust зовёт это раз в кадр, как браузер. Работа, назначенная в кадре, попадает в следующий. Время кадра
   * — то же, что у `performance.now()`, как и в браузере: иначе таймеры (их срок считается от `performance.now()`)
   * разошлись бы с кадрами на время загрузки игры. При записи же время ведёт окно, и часы стоят на нём.
   */
  globalThis.__kharos_frame = (milliseconds) => {
    const now = options.fixed ? milliseconds : performance.now()
    clock = now
    const callbacks = [...pending.values()]
    pending.clear()
    globalThis.__kharos_timers(now)
    for (const callback of callbacks) callback(now)
  }

  /**
   * Размер окна: игра узнаёт об этом так же, как в браузере — из размеров холста. Размер приходит физическими
   * пикселями, а плотность (ratio) говорит, сколько их в пикселе окна: холст занимает всё окно, а сколько в нём
   * пикселей отрисовки, игра решает сама (см. renderer.resize).
   */
  globalThis.__kharos_resize = (width, height, ratio = 1) => {
    globalThis.devicePixelRatio = ratio
    canvas.clientWidth = width / ratio
    canvas.clientHeight = height / ratio
    canvas.width = width
    canvas.height = height
    globalThis.innerWidth = canvas.clientWidth
    globalThis.innerHeight = canvas.clientHeight
    globalThis.dispatchEvent?.(new Event('resize'))
  }

  /**
   * Часы игры: монотонные миллисекунды, как `performance.now()` в браузере. При записи время ведёт окно ровным шагом
   * (см. `src/record.rs`), и часы стоят на времени кадра: иначе таймеры и игра разошлись бы с кадрами ролика.
   */
  const started = Date.now()
  globalThis.performance = {
    now: options.fixed ? () => clock : () => Date.now() - started,
  }

  /** Игра встала и рисует мир — с этого кадра запись пишет ролик. */
  globalThis.__kharos_roll = () => core.ops.op_record_roll()

  globalThis.console = {
    log: (...values) => core.ops.op_gl_log(values.map(String).join(' ')),
    info: (...values) => core.ops.op_gl_log(values.map(String).join(' ')),
    warn: (...values) => core.ops.op_gl_log(values.map(String).join(' ')),
    error: (...values) => core.ops.op_gl_log(values.map(String).join(' ')),
    debug: () => {},
  }

  /** Таймеры: игра пользуется ими для отложенной работы (перерисовки, подсказки, звук). Работают по кадру. */
  const timers = new Map()
  globalThis.setTimeout = (callback, delay = 0, ...args) => {
    const handle = nextHandle++
    const due = performance.now() + Math.max(0, delay)
    timers.set(handle, { due, callback: () => callback(...args) })
    return handle
  }
  globalThis.clearTimeout = (handle) => timers.delete(handle)
  globalThis.setInterval = (callback, delay = 16, ...args) => {
    const handle = nextHandle++
    const step = Math.max(1, delay)
    let due = performance.now() + step
    timers.set(handle, { due, repeat: step, tick: () => callback(...args), repeatable: true })
    return handle
  }
  globalThis.clearInterval = globalThis.clearTimeout

  /** Отложенная работа — тоже в кадре: отдельного потока таймеров здесь нет. */
  globalThis.__kharos_timers = (milliseconds) => {
    // Список берётся заранее: таймер, заведённый таймером (`setTimeout(…, 0)` подряд), ждёт следующего кадра, а не
    // крутится в этом без конца.
    for (const [handle, timer] of [...timers]) {
      if (!timers.has(handle) || milliseconds < timer.due) continue
      if (timer.repeatable) timer.due = milliseconds + timer.repeat
      else timers.delete(handle)
      try {
        ;(timer.tick ?? timer.callback)()
      } catch (error) {
        globalThis.console.error(String(error?.stack ?? error))
      }
    }
  }

  /**
   * Холст окна: общий на всю игру — как `<canvas>` на странице. Размеров у него два рода, как и в браузере:
   * clientWidth — сколько он занимает на странице (в пикселях окна), width — сколько в нём пикселей отрисовки.
   */
  const canvas = {
    width: 0,
    height: 0,
    clientWidth: 0,
    clientHeight: 0,
    /** Рамки у холста нет: интерфейс читает её у холста окна (см. Hud.tsx). */
    clientLeft: 0,
    clientTop: 0,
    gl: null,
    /** Холст окна как место страницы: ему достаются события указателя (см. `__kharos_input`). */
    element: null,
    style: {},
    getContext: (name) => (name === 'webgl2' ? (canvas.gl ??= new WebGL2RenderingContext()) : null),
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
    /** Холст занимает всё окно: страницы, на которой он стоял бы иначе, здесь нет. */
    getBoundingClientRect: () => ({
      left: 0,
      top: 0,
      right: canvas.clientWidth,
      bottom: canvas.clientHeight,
      width: canvas.clientWidth,
      height: canvas.clientHeight,
    }),
  }

  /**
   * Свойства стиля: интерфейс кладёт сюда переменные скина и места панелей (`--skin-panel`, `--chat-left`), а
   * читает их раскладка, когда рисует интерфейс. Свойства хранятся теми строками, какими их записал интерфейс:
   * разбирать `url(...)` незачем, пока интерфейс не рисуется.
   */
  const styleOf = () => {
    const values = new Map()
    return {
      setProperty: (name, value) => values.set(name, String(value)),
      getPropertyValue: (name) => values.get(name) ?? '',
      removeProperty: (name) => void values.delete(name),
    }
  }

  /**
   * Слушатели событий — то, чем страница раздаёт ввод: у окна, документа, холста окна и прочих мест они свои.
   * Слушатель — функция или объект с `handleEvent`, как у браузера; ошибка в одном не мешает остальным.
   */
  const listening = (owner) => {
    const listeners = new Map()
    owner.addEventListener = (type, listener) => {
      if (!listener) return
      if (!listeners.has(type)) listeners.set(type, new Set())
      listeners.get(type).add(listener)
    }
    owner.removeEventListener = (type, listener) => void listeners.get(type)?.delete(listener)
    owner.dispatchEvent = (event) => {
      event.currentTarget = owner
      for (const listener of [...(listeners.get(event.type) ?? [])]) {
        if (event.stopped) break
        try {
          if (typeof listener === 'function') listener.call(owner, event)
          else listener.handleEvent(event)
        } catch (error) {
          globalThis.console.error(String(error?.stack ?? error))
        }
      }
      return !event.defaultPrevented
    }
    return owner
  }

  /** Место на странице, которой пока нет: игре нужно, чтобы у него были слушатели и стиль. Разметку даст интерфейс. */
  const element = () => listening({ style: styleOf() })

  globalThis.window = globalThis
  /** Размер окна и плотность его пикселей: их выставляет `__kharos_resize`, когда окно меняется. */
  globalThis.innerWidth = 0
  globalThis.innerHeight = 0
  globalThis.devicePixelRatio = 1
  listening(globalThis)

  /** События — как у страницы: тип, поля из второго аргумента, отмена действия и остановка всплытия. */
  globalThis.Event = class Event {
    constructor(type, init = {}) {
      Object.assign(this, init)
      this.type = type
      this.defaultPrevented = false
      this.stopped = false
      this.timeStamp = performance.now()
    }
    preventDefault() {
      this.defaultPrevented = true
    }
    stopPropagation() {
      this.stopped = true
    }
    stopImmediatePropagation() {
      this.stopped = true
    }
  }
  globalThis.UIEvent ??= class UIEvent extends Event {}
  globalThis.MouseEvent ??= class MouseEvent extends UIEvent {}
  globalThis.PointerEvent ??= class PointerEvent extends MouseEvent {}
  globalThis.WheelEvent ??= class WheelEvent extends MouseEvent {}
  globalThis.KeyboardEvent ??= class KeyboardEvent extends UIEvent {}
  /** Виды мест страницы: игра проверяет ими, куда пришло нажатие (в поле ввода клавиши игре не достаются). */
  globalThis.Node ??= class Node {}
  globalThis.Element ??= class Element extends Node {}
  globalThis.HTMLElement ??= class HTMLElement extends Element {}
  globalThis.HTMLInputElement ??= class HTMLInputElement extends HTMLElement {}
  globalThis.HTMLTextAreaElement ??= class HTMLTextAreaElement extends HTMLElement {}

  /**
   * Хранилище страницы — в памяти: настройки и сохранения игра пишет в `localStorage`, а у окна его пока нет. Живёт
   * до закрытия окна; на диск оно ляжет вместе со сборками (пункт 10.5 плана).
   */
  const stored = new Map()
  globalThis.localStorage ??= {
    getItem: (key) => (stored.has(String(key)) ? stored.get(String(key)) : null),
    setItem: (key, value) => void stored.set(String(key), String(value)),
    removeItem: (key) => void stored.delete(String(key)),
    clear: () => stored.clear(),
    key: (index) => [...stored.keys()][index] ?? null,
    get length() {
      return stored.size
    },
  }

  globalThis.document = listening({
    hidden: false,
    visibilityState: 'visible',
    body: element(),
    activeElement: null,
    // Корень документа: на нём живут переменные интерфейса (`--skin-*`) и язык строк (см. `i18n`).
    documentElement: { ...element(), lang: '' },
    // Холст на всю игру здесь не один: холстом окна становится тот, у которого спросят WebGL 2 (см. `stage.ts`),
    // а прочие — вычислители интерфейса: на них рисуются скины, значки, портреты и мини-карта (см. `src/canvas`).
    createElement: (name) => (name === 'canvas' ? new CanvasElement() : element()),
    getElementById: () => canvas,
    // Разметки нет: игра ищет на странице только панели интерфейса, а его в оболочке ещё нет.
    querySelector: () => null,
    querySelectorAll: () => [],
  })
  document.activeElement = document.body

  /**
   * Ввод от окна (см. `input` в `src/main.rs`): из него собираются события страницы — указатель и колесо на холсте
   * окна, клавиши на документе — и всплывают дальше к документу и окну, как в браузере. Указатель приходит
   * физическими пикселями окна, а игре нужны пиксели страницы: холст занимает всё окно, так что его место и место на
   * странице совпадают.
   */
  const input = { x: 0, y: 0, buttons: 0, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false }
  /** Бит кнопки в `buttons` по её номеру: у страницы правая — 2, средняя — 4. */
  const BUTTON_BITS = [1, 4, 2, 8, 16]
  const bubble = (event, ...owners) => {
    for (const owner of owners) {
      if (event.stopped) break
      owner?.dispatchEvent(event)
    }
  }
  const modifiers = () => ({ shiftKey: input.shiftKey, ctrlKey: input.ctrlKey, altKey: input.altKey, metaKey: input.metaKey })
  globalThis.__kharos_input = (kind, a, b, c, d) => {
    const surface = canvas.element
    const pointer = (Kind, type, extra = {}) =>
      new Kind(type, {
        clientX: input.x,
        clientY: input.y,
        offsetX: input.x,
        offsetY: input.y,
        pageX: input.x,
        pageY: input.y,
        button: -1,
        buttons: input.buttons,
        pointerId: 1,
        pointerType: 'mouse',
        isPrimary: true,
        target: surface,
        ...modifiers(),
        ...extra,
      })
    switch (kind) {
      case 'move': {
        const ratio = globalThis.devicePixelRatio || 1
        input.x = a / ratio
        input.y = b / ratio
        bubble(pointer(PointerEvent, 'pointermove'), surface, document, globalThis)
        break
      }
      case 'leave':
        surface?.dispatchEvent(pointer(PointerEvent, 'pointerleave'))
        break
      case 'down':
        input.buttons |= BUTTON_BITS[a] ?? 0
        bubble(pointer(PointerEvent, 'pointerdown', { button: a }), surface, document, globalThis)
        if (a === 2) bubble(pointer(MouseEvent, 'contextmenu', { button: a }), surface, document, globalThis)
        break
      case 'up':
        input.buttons &= ~(BUTTON_BITS[a] ?? 0)
        bubble(pointer(PointerEvent, 'pointerup', { button: a }), surface, document, globalThis)
        break
      case 'wheel':
        bubble(pointer(WheelEvent, 'wheel', { deltaX: a, deltaY: b, deltaZ: 0, deltaMode: 0 }), surface, document, globalThis)
        break
      case 'key': {
        // Модификаторы — и по самой клавише: смена модификаторов может прийти от окна позже нажатия.
        const held = { Shift: 'shiftKey', Control: 'ctrlKey', Alt: 'altKey', Meta: 'metaKey', Super: 'metaKey' }[c]
        if (held) input[held] = a
        const event = new KeyboardEvent(a ? 'keydown' : 'keyup', { code: b, key: c, repeat: d, target: document.body, ...modifiers() })
        bubble(event, document.body, document, globalThis)
        break
      }
      case 'modifiers':
        Object.assign(input, { shiftKey: a, ctrlKey: b, altKey: c, metaKey: d })
        break
      case 'blur':
        input.buttons = 0
        globalThis.dispatchEvent(new Event('blur'))
        break
    }
  }

  /** Адресной строки у окна нет: игра читает из адреса только отладочные ключи, и тех нет. */
  globalThis.location = { href: 'kharos://native/', protocol: 'kharos:', host: 'native', pathname: '/', search: '', hash: '' }

  /**
   * Разбор «a=1&b=2». Игре он нужен, чтобы достать из адреса отладочные ключи (`?scene=`, `?hour=`), а у голого V8
   * такого разбора нет — его даёт окружение, как и перевод текста ниже.
   */
  globalThis.URLSearchParams ??= class URLSearchParams {
    constructor(init = '') {
      /** Значения по именам: одно имя встречается в адресе сколько угодно раз, поэтому у каждого свой список. */
      this.values = new Map()
      for (const pair of String(init).replace(/^\?/, '').split('&')) {
        if (!pair) continue
        const [name, value = ''] = pair.split('=')
        const key = decodeURIComponent(name.replace(/\+/g, ' '))
        this.values.set(key, [...(this.values.get(key) ?? []), decodeURIComponent(value.replace(/\+/g, ' '))])
      }
    }
    get(name) {
      return this.values.get(name)?.[0] ?? null
    }
    getAll(name) {
      return [...(this.values.get(name) ?? [])]
    }
    has(name) {
      return this.values.has(name)
    }
  }

  /**
   * Текст в байты и обратно: этим игра пишет и читает сохранения. Перевод делает сам движок — операция берёт строку у
   * него, а не повторяет UTF-8 на JS (см. `src/web.rs`).
   */
  globalThis.TextEncoder ??= class TextEncoder {
    constructor() {
      this.encoding = 'utf-8'
    }
    encode(text = '') {
      return core.ops.op_text_encode(text)
    }
  }
  globalThis.TextDecoder ??= class TextDecoder {
    constructor(label = 'utf-8') {
      this.encoding = label
    }
    decode(bytes = new Uint8Array(0)) {
      return core.ops.op_text_decode(bytes)
    }
  }

  /** Склейка кусков байтов в один массив. */
  const joined = (chunks) => {
    const out = new Uint8Array(chunks.reduce((sum, chunk) => sum + chunk.byteLength, 0))
    let at = 0
    for (const chunk of chunks) {
      out.set(chunk instanceof Uint8Array ? chunk : new Uint8Array(chunk.buffer ?? chunk, chunk.byteOffset ?? 0, chunk.byteLength), at)
      at += chunk.byteLength
    }
    return out
  }

  /**
   * Потоки — в том объёме, в каком их берёт игра: источник отдаёт куски (`pull`/`start`), поток сжатия перерабатывает
   * их целиком, а `Response` собирает итог (так сжимаются карта для клиента и сохранения, см. `src/save/file.ts`).
   * Порциями здесь ничего не идёт: кусков мало, и сжимает их Rust (`op_compress`) одним вызовом.
   */
  globalThis.ReadableStream ??= class ReadableStream {
    constructor(source = {}) {
      this.source = source
    }
    /** Все куски источника по порядку — до `close()`. */
    async collect() {
      const chunks = []
      let closed = false
      const controller = { enqueue: (chunk) => chunks.push(chunk), close: () => (closed = true), error: (error) => { throw error } }
      await this.source.start?.(controller)
      while (!closed && this.source.pull) await this.source.pull(controller)
      return chunks
    }
    pipeThrough(transform) {
      return transform.from(this)
    }
  }
  const compression = (inflate) =>
    class {
      constructor(format) {
        this.format = format
      }
      from(readable) {
        return new ReadableStream({
          start: async (controller) => {
            controller.enqueue(core.ops.op_compress(joined(await readable.collect()), this.format, inflate))
            controller.close()
          },
        })
      }
    }
  globalThis.CompressionStream ??= compression(false)
  globalThis.DecompressionStream ??= compression(true)
  globalThis.Response ??= class Response {
    constructor(body = null) {
      this.body = body
    }
    async bytes() {
      const body = this.body
      if (body instanceof ReadableStream) return joined(await body.collect())
      if (typeof body === 'string') return new TextEncoder().encode(body)
      if (body instanceof ArrayBuffer) return new Uint8Array(body)
      if (ArrayBuffer.isView(body)) return new Uint8Array(body.buffer, body.byteOffset, body.byteLength).slice()
      return new Uint8Array(0)
    }
    async arrayBuffer() {
      return (await this.bytes()).buffer
    }
    async text() {
      return new TextDecoder().decode(await this.bytes())
    }
    async json() {
      return JSON.parse(await this.text())
    }
  }

  /**
   * Сведения о браузере: язык строк (по нему `i18n` выбирает словарь — его окно берёт из `LANG`) и то, что
   * пользователь уже нажимал на страницу: звук браузер даёт только после этого, а у окна такого запрета нет.
   */
  globalThis.navigator ??= {
    language: options.lang ?? 'ru',
    languages: [options.lang ?? 'ru'],
    userAgent: 'Kharos native',
    hardwareConcurrency: 1,
    userActivation: { hasBeenActive: true, isActive: true },
  }

  /** Цвет в число 0xRRGGBBAA: игра пишет цвета строками (`#1c2b3e`, `rgba(...)`), а холст берёт числами. */
  const colorOf = (value) => {
    const text = String(value).trim()
    const byte = (number) => Math.max(0, Math.min(255, Math.round(Number(number) || 0)))
    if (text.startsWith('#')) {
      const digits = text.slice(1)
      // Короткая запись (`#abc`) удваивает каждый знак, длинная берётся как есть; альфа — если она записана.
      const full = digits.length <= 4 ? [...digits].map((digit) => digit + digit).join('') : digits
      const part = (index) => parseInt(full.slice(index * 2, index * 2 + 2) || '0', 16) || 0
      return ((part(0) << 24) | (part(1) << 16) | (part(2) << 8) | (full.length >= 8 ? part(3) : 255)) >>> 0
    }
    const parts = text.match(/[0-9.]+/g) ?? []
    const alpha = parts.length > 3 ? byte(Number(parts[3]) * 255) : 255
    return ((byte(parts[0]) << 24) | (byte(parts[1]) << 16) | (byte(parts[2]) << 8) | alpha) >>> 0
  }

  /** Картинка для двумерного холста: мини-карта и значки заводят её заранее, а рисует её интерфейс. */
  globalThis.ImageData ??= class ImageData {
    constructor(first, second, third) {
      // Два вида вызова, как у браузера: новая картинка нужного размера или готовые пиксели с размерами.
      if (typeof first === 'number') {
        this.width = first
        this.height = second ?? first
        this.data = new Uint8ClampedArray(this.width * this.height * 4)
        return
      }
      this.data = first instanceof Uint8ClampedArray ? first : new Uint8ClampedArray(first)
      this.width = second ?? 0
      this.height = third ?? (this.width ? this.data.length / 4 / this.width : 0)
    }
  }

  /**
   * Холст: то же, что `<canvas>` в браузере, но пиксели его лежат в Rust (см. `src/canvas`) — игра пользуется холстом
   * как вычислителем: рисует на нём картинки кодом (скины, портреты, значки, мини-карту) и отдаёт их строкой
   * (`toDataURL`). Холст, у которого спросят WebGL 2, становится холстом окна: он в игре один (см. `stage.ts`), и
   * размеры его — размеры окна, а не холста.
   */
  class CanvasElement {
    constructor(width = 300, height = 150) {
      /** Номер холста в Rust: за ним лежат пиксели. */
      this.id = core.ops.op_canvas_create(width, height)
      this.pixels = { width, height }
      this.context = null
      this.isWindow = false
      this.style = {}
      // Холст окна получает ввод (см. `__kharos_input`), прочим слушатели нужны лишь для порядка.
      listening(this)
    }

    get width() {
      return this.isWindow ? canvas.width : this.pixels.width
    }

    set width(value) {
      const size = Math.max(0, Math.round(value))
      if (this.isWindow) {
        canvas.width = size
        return
      }
      this.resize(size, this.pixels.height)
    }

    get height() {
      return this.isWindow ? canvas.height : this.pixels.height
    }

    set height(value) {
      const size = Math.max(0, Math.round(value))
      if (this.isWindow) {
        canvas.height = size
        return
      }
      this.resize(this.pixels.width, size)
    }

    /** Смена размера холста: пиксели в Rust заводятся заново, как и у холста в браузере. */
    resize(width, height) {
      this.pixels.width = width
      this.pixels.height = height
      core.ops.op_canvas_resize(this.id, width, height)
    }

    get clientWidth() {
      return this.isWindow ? canvas.clientWidth : this.pixels.width
    }

    get clientHeight() {
      return this.isWindow ? canvas.clientHeight : this.pixels.height
    }

    /** Рамки у холста нет, а читает её интерфейс (см. Hud.tsx): без неё по месту указателя вышло бы «не число». */
    get clientLeft() {
      return 0
    }

    get clientTop() {
      return 0
    }

    getContext(name) {
      if (name === '2d') return (this.context ??= new CanvasRenderingContext2D(this))
      if (name === 'webgl2') {
        // Единственный холст с WebGL 2 — холст окна: так его берёт игра (см. `stage.ts`), и проходы рисуют в него.
        this.isWindow = true
        canvas.element = this
        return (canvas.gl ??= new WebGL2RenderingContext())
      }
      return null
    }

    /** Холст окна занимает всё окно, вычислитель стоит в левом верхнем углу своего размера. */
    getBoundingClientRect() {
      if (this.isWindow) return canvas.getBoundingClientRect()
      const { width, height } = this.pixels
      return { left: 0, top: 0, right: width, bottom: height, width, height }
    }

    /** Картинка холста строкой: её берут и стили (`url(...)`), и `<img>` — так интерфейс получает значки и портреты. */
    toDataURL(type = 'image/png') {
      if (this.isWindow) return ''
      return `data:${type};base64,${core.ops.op_canvas_png(this.id)}`
    }

    /** Захват указателя: у окна указатель и так один и не теряется за краем холста. */
    setPointerCapture() {}
    releasePointerCapture() {}
    hasPointerCapture() {
      return false
    }

    focus() {}
    blur() {}
  }

  /**
   * Контекст двумерного холста: то, чем игра рисует. Состояние (цвета, толщина линии, прозрачность) ведёт эта
   * обёртка, как и в браузере, а рисование уходит в Rust — там сами пиксели. Путь копится здесь и уходит одной
   * посылкой; дуги дробятся здесь же: растеризатору довольно отрезков, как и пиксельной графике.
   */
  class CanvasRenderingContext2D {
    constructor(canvas) {
      this.canvas = canvas
      this.fillStyle = '#000000'
      this.strokeStyle = '#000000'
      this.lineWidth = 1
      this.globalAlpha = 1
      this.imageSmoothingEnabled = true
      this.imageSmoothingQuality = 'low'
      /** Путь тройками: вид точки (0 — начало подпути, 1 — продолжение), x, y. */
      this._path = []
    }

    set fillStyle(value) {
      this._fill = String(value)
      this._fillColor = colorOf(value)
    }

    get fillStyle() {
      return this._fill
    }

    set strokeStyle(value) {
      this._stroke = String(value)
      this._strokeColor = colorOf(value)
    }

    get strokeStyle() {
      return this._stroke
    }

    fillRect(x, y, width, height) {
      core.ops.op_canvas_fill_rect(this.canvas.id, x, y, width, height, this._fillColor, this.globalAlpha)
    }

    strokeRect(x, y, width, height) {
      core.ops.op_canvas_stroke_rect(this.canvas.id, x, y, width, height, this.lineWidth, this._strokeColor, this.globalAlpha)
    }

    beginPath() {
      this._path = []
    }

    moveTo(x, y) {
      this._path.push(0, x, y)
    }

    lineTo(x, y) {
      this._path.push(1, x, y)
    }

    /**
     * Дуга: дробится на отрезки по половине пикселя дуги — так кольцо сигнала выходит ровным. Полный оборот от нуля
     * до двух «пи» даёт круг (см. `minimap.ts`). Начало подпути дуга открывает сама, если до неё пути не было.
     */
    arc(x, y, radius, from = 0, to = Math.PI * 2) {
      const steps = Math.max(8, Math.ceil((Math.abs(to - from) * radius) / 0.5))
      const opening = this._path.length === 0
      for (let step = 0; step <= steps; step++) {
        const angle = from + ((to - from) * step) / steps
        this._path.push(step === 0 && opening ? 0 : 1, x + Math.cos(angle) * radius, y + Math.sin(angle) * radius)
      }
    }

    stroke() {
      core.ops.op_canvas_path(this.canvas.id, bytes(Float32Array.from(this._path)), this._strokeColor, this.lineWidth, this.globalAlpha, false)
    }

    fill() {
      core.ops.op_canvas_path(this.canvas.id, bytes(Float32Array.from(this._path)), this._fillColor, this.lineWidth, this.globalAlpha, true)
    }

    putImageData(image, x, y) {
      const pixels = image.data instanceof Uint8ClampedArray ? image.data : new Uint8ClampedArray(image.data)
      core.ops.op_canvas_put_image_data(this.canvas.id, Math.round(x), Math.round(y), image.width, image.height, bytes(pixels))
    }

    getImageData(x, y, width, height) {
      const pixels = core.ops.op_canvas_get_image_data(this.canvas.id, Math.round(x), Math.round(y), width, height)
      return new ImageData(new Uint8ClampedArray(pixels), width, height)
    }

    createImageData(width, height) {
      return new ImageData(width, height)
    }

    /**
     * Перенос картинки с другого холста. Три вида вызова, как у браузера: целиком, по месту и по месту с размером —
     * ими игра и увеличивает портреты (см. `portraits.ts`), и кладёт мини-карту (см. `minimap.ts`).
     */
    drawImage(source, ...rest) {
      const id = source?.id ?? -1
      const [sx, sy, sw, sh, dx, dy, dw, dh] = rest
      const smoothing = this.imageSmoothingEnabled
      if (rest.length === 2) {
        core.ops.op_canvas_draw_image(this.canvas.id, id, 0, 0, source.width, source.height, sx, sy, source.width, source.height, smoothing, this.globalAlpha)
      } else if (rest.length === 4) {
        core.ops.op_canvas_draw_image(this.canvas.id, id, 0, 0, source.width, source.height, sx, sy, sw, sh, smoothing, this.globalAlpha)
      } else {
        core.ops.op_canvas_draw_image(this.canvas.id, id, sx, sy, sw, sh, dx, dy, dw, dh, smoothing, this.globalAlpha)
      }
    }
  }

  /** Двумерный холст вне страницы: у игры их заводят мини-карта и значки. */
  globalThis.OffscreenCanvas ??= class OffscreenCanvas extends CanvasElement {}

  /**
   * Контекст WebGL 2: то, чем игра разговаривает с видеокартой. Связывание буферов, текстур и мест атрибутов
   * держит драйвер — обёртка лишь переводит вызовы. Своё она хранит только там, где без этого перевод неполон:
   * размер экрана и связанный сейчас буфер вершин (его требует `vertexAttribPointer`).
   */
  class WebGL2RenderingContext {
    constructor() {
      /** Буфер вершин, связанный сейчас: `vertexAttribPointer` берёт данные из него. */
      this.boundArrayBuffer = 0
    }

    /** Сколько в холсте пикселей отрисовки. Их задаёт игра, подгоняя холст под окно (см. renderer.resize). */
    get drawingBufferWidth() {
      return canvas.width
    }

    get drawingBufferHeight() {
      return canvas.height
    }

    bindBuffer(target, buffer) {
      if (target === GL.ARRAY_BUFFER) this.boundArrayBuffer = buffer ?? 0
      call(ops.bindBuffer, buffer ?? 0, target)
    }

    bindVertexArray(array) {
      call(ops.bindVertexArray, array ?? 0)
    }

    bindTexture(target, texture) {
      call(ops.bindTexture, target, texture ?? 0)
    }

    bindFramebuffer(target, framebuffer) {
      call(ops.bindFramebuffer, framebuffer ?? 0, target)
    }

    activeTexture(unit) {
      call(ops.activeTexture, unit)
    }

    useProgram(program) {
      call(ops.useProgram, program ?? 0)
    }

    enable(capability) {
      call(ops.enable, capability)
    }

    disable(capability) {
      call(ops.disable, capability)
    }

    blendFunc(source, destination) {
      call(ops.blendFunc, source, destination)
    }

    clear(bits) {
      call(ops.clear, bits)
    }

    clearColor(red, green, blue, alpha) {
      call(ops.clearColor, red, green, blue, alpha)
    }

    viewport(x, y, width, height) {
      call(ops.viewport, x, y, width, height)
    }

    pixelStorei(name, value) {
      call(ops.pixelStorei, name, value)
    }

    flush() {
      call(ops.flush)
    }

    finish() {
      call(ops.finish)
    }

    /** Потерять контекст здесь нельзя: окно живёт вместе с игрой, и восстанавливать нечего. */
    isContextLost() {
      return false
    }

    createBuffer() {
      return call(ops.createBuffer)
    }

    createVertexArray() {
      return call(ops.createVertexArray)
    }

    createTexture() {
      return call(ops.createTexture)
    }

    createProgram() {
      return call(ops.createProgram)
    }

    createShader(type) {
      return call(ops.createShader, type)
    }

    createFramebuffer() {
      return call(ops.createFramebuffer)
    }

    deleteBuffer(buffer) {
      call(ops.deleteBuffer, buffer ?? 0)
    }

    deleteVertexArray(array) {
      call(ops.deleteVertexArray, array ?? 0)
    }

    deleteTexture(texture) {
      call(ops.deleteTexture, texture ?? 0)
    }

    deleteProgram(program) {
      call(ops.deleteProgram, program ?? 0)
    }

    deleteShader(shader) {
      call(ops.deleteShader, shader ?? 0)
    }

    deleteFramebuffer(framebuffer) {
      call(ops.deleteFramebuffer, framebuffer ?? 0)
    }

    shaderSource(shader, source) {
      // Версию GLSL переводит Rust: игра пишет шейдеры для браузера (GLSL ES).
      core.ops.op_gl_shader_source(shader, source)
    }

    compileShader(shader) {
      call(ops.compileShader, shader)
    }

    attachShader(program, shader) {
      call(ops.attachShader, program, shader)
    }

    linkProgram(program) {
      call(ops.linkProgram, program)
    }

    bindAttribLocation(program, index, name) {
      core.ops.op_gl_bind_attrib_location(program, index, name)
    }

    getShaderParameter(shader, name) {
      return core.ops.op_gl_get_shader_parameter(shader, name) !== 0
    }

    getShaderInfoLog(shader) {
      return core.ops.op_gl_get_shader_info_log(shader)
    }

    getProgramParameter(program, name) {
      const value = core.ops.op_gl_get_program_parameter(program, name)
      return name === GL.LINK_STATUS ? value !== 0 : value
    }

    getProgramInfoLog(program) {
      return core.ops.op_gl_get_program_info_log(program)
    }

    getActiveUniform(program, index) {
      return core.ops.op_gl_get_active_uniform(program, index)
    }

    /** Номера нет — значит юниформа в шейдере нет: в WebGL это `null`, и игра на это опирается. */
    getUniformLocation(program, name) {
      const location = core.ops.op_gl_get_uniform_location(program, name)
      return location > 0 ? location : null
    }

    getAttribLocation(program, name) {
      return core.ops.op_gl_get_attrib_location(program, name)
    }

    enableVertexAttribArray(index) {
      call(ops.enableVertexAttribArray, index)
    }

    disableVertexAttribArray(index) {
      call(ops.disableVertexAttribArray, index)
    }

    vertexAttribPointer(index, size, type, normalized, stride, offset) {
      // Драйвер берёт данные из связанного буфера вершин, поэтому напоминаем ему, какой связала игра.
      call(ops.bindBuffer, this.boundArrayBuffer, GL.ARRAY_BUFFER)
      call(ops.vertexAttribPointer, index, size, type, normalized ? 1 : 0, stride, offset)
    }

    vertexAttribDivisor(index, divisor) {
      call(ops.vertexAttribDivisor, index, divisor)
    }

    /** Пиксели и вершины уходят в Rust без копирования: он читает ту же память V8, что и игра. */
    bufferData(target, data, usage) {
      if (typeof data === 'number') {
        core.ops.op_gl_buffer_data(target, this.boundArrayBuffer, new Uint8Array(data), usage)
        return
      }
      core.ops.op_gl_buffer_data(target, this.boundArrayBuffer, bytes(data), usage)
    }

    bufferSubData(target, offset, data, source = 0, length = undefined) {
      const count = length ?? data.length - source
      const view = new Uint8Array(data.buffer, data.byteOffset + source * data.BYTES_PER_ELEMENT, count * data.BYTES_PER_ELEMENT)
      core.ops.op_gl_buffer_sub_data(target, this.boundArrayBuffer, offset, view)
    }

    texImage2D(target, level, internal, width, height, border, format, type, pixels) {
      core.ops.op_gl_tex_image_2d(target, level, internal, width, height, border, format, type, pixels ? bytes(pixels) : new Uint8Array(0))
    }

    texStorage2D(target, levels, internal, width, height) {
      call(ops.texStorage2D, target, levels, internal, width, height)
    }

    texSubImage2D(target, level, x, y, width, height, format, type, pixels) {
      core.ops.op_gl_tex_sub_image_2d(target, level, x, y, width, height, format, type, pixels ? bytes(pixels) : new Uint8Array(0))
    }

    texParameteri(target, name, value) {
      call(ops.texParameteri, target, name, value)
    }

    generateMipmap(target) {
      call(ops.generateMipmap, target, 0)
    }

    framebufferTexture2D(target, attachment, textarget, texture, level) {
      call(ops.framebufferTexture2D, target, attachment, textarget, texture ?? 0, level)
    }

    drawArrays(mode, first, count) {
      call(ops.drawArrays, mode, first, count)
    }

    drawArraysInstanced(mode, first, count, instances) {
      call(ops.drawArraysInstanced, mode, first, count, instances)
    }

    uniform1f(location, value) {
      if (location === null) return
      call(ops.uniform1f, location, value)
    }

    uniform1i(location, value) {
      if (location === null) return
      call(ops.uniform1i, location, value)
    }

    /**
     * Юниформы-массивы: игра задаёт их каждым кадром (вид кадра и масштаб) и передаёт в том числе обычными
     * массивами чисел — цвета (см. `UniformValue`). У обычного массива нет ни буфера, ни байтов, поэтому в числа
     * его переводит обёртка: иначе юниформ остался бы нулевым, и огни рисовались бы чёрным, то есть никак.
     */
    uniformVector(location, value, size) {
      if (location === null) return
      const floats = value instanceof Float32Array ? value : Float32Array.from(value)
      core.ops.op_gl_uniform_fv(location, size, bytes(floats))
    }

    uniform1fv(location, value) {
      this.uniformVector(location, value, 1)
    }

    uniform2fv(location, value) {
      this.uniformVector(location, value, 2)
    }

    uniform3fv(location, value) {
      this.uniformVector(location, value, 3)
    }

    uniform4fv(location, value) {
      this.uniformVector(location, value, 4)
    }

    /** Забор: игра ждёт по нему, пока видеокарта выполнит отправленное. */
    fenceSync(condition, flags) {
      return call(ops.fenceSync, condition ?? 0, flags ?? 0)
    }

    clientWaitSync(sync, flags, timeout) {
      return call(ops.clientWaitSync, sync ?? 0, flags ?? 0, timeout ?? 0)
    }

    deleteSync(sync) {
      call(ops.deleteSync, sync ?? 0)
    }

    /** Чтение кадра с видеокарты: им записывается геймплей и проверяются проходы. */
    readPixels(x, y, width, height, format, type, pixels) {
      const read = core.ops.op_gl_read_pixels(x, y, width, height, format, type)
      if (pixels) pixels.set(read.subarray(0, pixels.length))
      return read
    }
  }

  // Константы — на самом классе и на его образцах: игра читает их как `gl.TEXTURE_2D`.
  for (const [name, value] of Object.entries(GL)) {
    WebGL2RenderingContext.prototype[name] = value
    WebGL2RenderingContext[name] = value
  }

  globalThis.WebGL2RenderingContext = WebGL2RenderingContext
})()
