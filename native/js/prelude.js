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
    UNPACK_FLIP_Y_WEBGL: 0x9240, UNPACK_PREMULTIPLY_ALPHA_WEBGL: 0x9241,
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

  /** Режимы наложения по порядку — тот же порядок у Rust (см. `blend` в `src/canvas/sheet.rs`). */
  const COMPOSITE = [
    'source-over', 'source-in', 'source-out', 'source-atop', 'destination-over', 'destination-in', 'destination-out',
    'destination-atop', 'lighter', 'copy', 'xor', 'multiply', 'screen', 'overlay', 'darken', 'lighten', 'color-dodge',
    'color-burn', 'hard-light', 'soft-light', 'difference', 'exclusion', 'hue', 'saturation', 'color', 'luminosity',
  ]
  const CAPS = ['butt', 'round', 'square']
  const JOINS = ['miter', 'round', 'bevel']
  const ALIGN = { start: 0, left: 0, center: 1, end: 2, right: 2 }
  const BASELINE = { alphabetic: 0, top: 1, hanging: 2, middle: 3, ideographic: 4, bottom: 5 }

  /**
   * Путь командами (см. `path` в `src/canvas/sheet.rs`): 0 начало, 1 линия, 2 и 3 — кривые, 4 замыкание. Дуги и
   * эллипсы дробятся здесь на отрезки по полпикселя: растеризатору довольно линий, а края выходят ровными.
   */
  class Path2D {
    constructor(from) {
      this._commands = from instanceof Path2D ? [...from._commands] : []
      this._open = false
      this._x = 0
      this._y = 0
    }
    _point(command, ...numbers) {
      this._commands.push(command, ...numbers)
      this._x = numbers[numbers.length - 2]
      this._y = numbers[numbers.length - 1]
    }
    moveTo(x, y) {
      this._point(0, x, y)
      this._open = true
    }
    lineTo(x, y) {
      this._point(this._open ? 1 : 0, x, y)
      this._open = true
    }
    quadraticCurveTo(x1, y1, x, y) {
      if (!this._open) this.moveTo(x1, y1)
      this._point(2, x1, y1, x, y)
    }
    bezierCurveTo(x1, y1, x2, y2, x, y) {
      if (!this._open) this.moveTo(x1, y1)
      this._point(3, x1, y1, x2, y2, x, y)
    }
    closePath() {
      if (this._open) this._commands.push(4)
    }
    rect(x, y, width, height) {
      this.moveTo(x, y)
      this.lineTo(x + width, y)
      this.lineTo(x + width, y + height)
      this.lineTo(x, y + height)
      this.closePath()
      this.moveTo(x, y)
    }
    roundRect(x, y, width, height, radii = 0) {
      const radius = Math.min(Math.abs(Array.isArray(radii) ? radii[0] ?? 0 : radii), Math.abs(width) / 2, Math.abs(height) / 2)
      if (!radius) return this.rect(x, y, width, height)
      this.moveTo(x + radius, y)
      this.arcTo(x + width, y, x + width, y + height, radius)
      this.arcTo(x + width, y + height, x, y + height, radius)
      this.arcTo(x, y + height, x, y, radius)
      this.arcTo(x, y, x + width, y, radius)
      this.closePath()
    }
    ellipse(x, y, radiusX, radiusY, rotation, from, to, counter = false) {
      // Сколько пройти и куда: полный оборот, если просят оборот и больше, иначе — остаток в нужную сторону.
      const turn = Math.PI * 2
      const mod = (value) => ((value % turn) + turn) % turn
      const sweep = !counter ? (to - from >= turn ? turn : mod(to - from)) : from - to >= turn ? -turn : -mod(from - to)
      const steps = Math.max(8, Math.ceil((Math.abs(sweep) * Math.max(radiusX, radiusY)) / 0.5))
      const cos = Math.cos(rotation)
      const sin = Math.sin(rotation)
      for (let step = 0; step <= steps; step++) {
        const angle = from + (sweep * step) / steps
        const px = Math.cos(angle) * radiusX
        const py = Math.sin(angle) * radiusY
        this.lineTo(x + px * cos - py * sin, y + px * sin + py * cos)
      }
    }
    arc(x, y, radius, from = 0, to = Math.PI * 2, counter = false) {
      this.ellipse(x, y, radius, radius, 0, from, to, counter)
    }
    /** Скругление угла между текущей точкой, (x1, y1) и (x2, y2) — дугой радиуса radius, касающейся обеих сторон. */
    arcTo(x1, y1, x2, y2, radius) {
      if (!this._open) this.moveTo(x1, y1)
      const ax = this._x - x1, ay = this._y - y1, bx = x2 - x1, by = y2 - y1
      const la = Math.hypot(ax, ay), lb = Math.hypot(bx, by)
      if (!radius || !la || !lb) return this.lineTo(x1, y1)
      const ua = [ax / la, ay / la], ub = [bx / lb, by / lb]
      const angle = Math.acos(Math.max(-1, Math.min(1, ua[0] * ub[0] + ua[1] * ub[1])))
      // Стороны на одной прямой — скруглять нечего.
      if (angle < 1e-6 || Math.PI - angle < 1e-6) return this.lineTo(x1, y1)
      const along = radius / Math.tan(angle / 2)
      const start = [x1 + ua[0] * along, y1 + ua[1] * along]
      const end = [x1 + ub[0] * along, y1 + ub[1] * along]
      // Центр — на биссектрисе угла, на расстоянии radius / sin(угол / 2) от вершины.
      const bisector = [ua[0] + ub[0], ua[1] + ub[1]]
      const length = Math.hypot(...bisector)
      const distance = radius / Math.sin(angle / 2)
      const center = [x1 + (bisector[0] / length) * distance, y1 + (bisector[1] / length) * distance]
      const from = Math.atan2(start[1] - center[1], start[0] - center[0])
      const to = Math.atan2(end[1] - center[1], end[0] - center[0])
      // Дуга — короткая: её направление — по знаку кратчайшего поворота от начала к концу.
      const delta = Math.atan2(Math.sin(to - from), Math.cos(to - from))
      this.lineTo(...start)
      this.arc(center[0], center[1], radius, from, to, delta < 0)
    }
    _bytes() {
      return bytes(Float32Array.from(this._commands))
    }
  }
  globalThis.Path2D = Path2D

  /**
   * Контекст двумерного холста — Canvas 2D API. Состояние (цвета, линия, прозрачность, преобразование, обрезка) и его
   * стек ведёт эта обёртка, как и браузер, а рисование уходит в Rust (`src/canvas`) — там сами пиксели. Путь хранится
   * в координатах пользователя и рисуется с преобразованием на момент `fill`/`stroke`: так у страницы, если между
   * построением пути и рисованием преобразование не менялось, — а иначе интерфейс и не делает.
   */
  class CanvasRenderingContext2D {
    constructor(canvas) {
      this.canvas = canvas
      this._path = new Path2D()
      this._stack = []
      this._values = new Float64Array(14)
      this._reset()
    }

    _reset() {
      this.fillStyle = '#000000'
      this.strokeStyle = '#000000'
      this.lineWidth = 1
      this.lineCap = 'butt'
      this.lineJoin = 'miter'
      this.miterLimit = 10
      this.globalAlpha = 1
      this.globalCompositeOperation = 'source-over'
      this.imageSmoothingEnabled = true
      this.imageSmoothingQuality = 'low'
      this.font = '10px sans-serif'
      this.textAlign = 'start'
      this.textBaseline = 'alphabetic'
      this.direction = 'ltr'
      this._matrix = [1, 0, 0, 1, 0, 0]
      this._mask = 0
      this._dash = []
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

    /** Состояние одним массивом — порядок тот же, что у `State::read` в `src/canvas/sheet.rs`. */
    _state() {
      const values = this._values
      values.set(this._matrix, 0)
      values[6] = this.globalAlpha
      values[7] = this._mask
      values[8] = !this.imageSmoothingEnabled ? 0 : this.imageSmoothingQuality === 'low' ? 1 : 2
      values[9] = Math.max(0, COMPOSITE.indexOf(this.globalCompositeOperation))
      values[10] = this.lineWidth
      values[11] = Math.max(0, CAPS.indexOf(this.lineCap))
      values[12] = Math.max(0, JOINS.indexOf(this.lineJoin))
      values[13] = this.miterLimit
      return bytes(values)
    }

    // Стек состояний: обрезка — маской в Rust; маска, заведённая на снятом уровне, отпускается.
    save() {
      const { _matrix, _mask, _dash } = this
      const copy = {}
      for (const key of ['_fill', '_fillColor', '_stroke', '_strokeColor', 'lineWidth', 'lineCap', 'lineJoin', 'miterLimit', 'globalAlpha', 'globalCompositeOperation', 'imageSmoothingEnabled', 'imageSmoothingQuality', 'font', 'textAlign', 'textBaseline', 'direction']) copy[key] = this[key]
      this._stack.push({ copy, matrix: [..._matrix], mask: _mask, dash: [..._dash] })
    }
    restore() {
      const saved = this._stack.pop()
      if (!saved) return
      if (this._mask && this._mask !== saved.mask) core.ops.op_canvas_unclip(this._mask)
      Object.assign(this, saved.copy)
      this._matrix = saved.matrix
      this._mask = saved.mask
      this._dash = saved.dash
    }
    reset() {
      while (this._stack.length) this.restore()
      if (this._mask) core.ops.op_canvas_unclip(this._mask)
      this._reset()
      this._path = new Path2D()
      this.canvas.resize(this.canvas.width, this.canvas.height)
    }

    // Преобразование: матрица a, b, c, d, e, f — как у страницы.
    setTransform(a = 1, b = 0, c = 0, d = 1, e = 0, f = 0) {
      if (typeof a === 'object') ({ a = 1, b = 0, c = 0, d = 1, e = 0, f = 0 } = a)
      this._matrix = [a, b, c, d, e, f]
    }
    getTransform() {
      const [a, b, c, d, e, f] = this._matrix
      return { a, b, c, d, e, f, is2D: true, isIdentity: a === 1 && b === 0 && c === 0 && d === 1 && e === 0 && f === 0 }
    }
    resetTransform() {
      this._matrix = [1, 0, 0, 1, 0, 0]
    }
    transform(a, b, c, d, e, f) {
      const [A, B, C, D, E, F] = this._matrix
      this._matrix = [A * a + C * b, B * a + D * b, A * c + C * d, B * c + D * d, A * e + C * f + E, B * e + D * f + F]
    }
    translate(x, y) {
      this.transform(1, 0, 0, 1, x, y)
    }
    scale(x, y) {
      this.transform(x, 0, 0, y, 0, 0)
    }
    rotate(angle) {
      const cos = Math.cos(angle)
      const sin = Math.sin(angle)
      this.transform(cos, sin, -sin, cos, 0, 0)
    }

    // Пути.
    beginPath() {
      this._path = new Path2D()
    }
    moveTo(x, y) {
      this._path.moveTo(x, y)
    }
    lineTo(x, y) {
      this._path.lineTo(x, y)
    }
    quadraticCurveTo(...args) {
      this._path.quadraticCurveTo(...args)
    }
    bezierCurveTo(...args) {
      this._path.bezierCurveTo(...args)
    }
    arc(...args) {
      this._path.arc(...args)
    }
    arcTo(...args) {
      this._path.arcTo(...args)
    }
    ellipse(...args) {
      this._path.ellipse(...args)
    }
    rect(...args) {
      this._path.rect(...args)
    }
    roundRect(...args) {
      this._path.roundRect(...args)
    }
    closePath() {
      this._path.closePath()
    }
    /** Путь и правило заливки из аргументов: `fill()`, `fill('evenodd')`, `fill(path)`, `fill(path, 'evenodd')`. */
    _target(first, second) {
      return first instanceof Path2D ? [first, second === 'evenodd'] : [this._path, first === 'evenodd']
    }
    fill(first, second) {
      const [path, evenodd] = this._target(first, second)
      core.ops.op_canvas_fill(this.canvas.id, path._bytes(), this._state(), this._fillColor, evenodd)
    }
    stroke(path = this._path) {
      core.ops.op_canvas_stroke(this.canvas.id, path._bytes(), this._state(), this._strokeColor)
    }
    clip(first, second) {
      const [path, evenodd] = this._target(first, second)
      const previous = this._mask
      this._mask = core.ops.op_canvas_clip(this.canvas.id, path._bytes(), this._state(), evenodd)
      // Маска этого же уровня стека больше не нужна: новая уже включает её.
      if (previous && !this._stack.some((saved) => saved.mask === previous)) core.ops.op_canvas_unclip(previous)
    }
    setLineDash(dash) {
      this._dash = [...dash]
    }
    getLineDash() {
      return [...this._dash]
    }

    // Прямоугольники.
    fillRect(x, y, width, height) {
      const path = new Path2D()
      path.rect(x, y, width, height)
      this.fill(path)
    }
    strokeRect(x, y, width, height) {
      const path = new Path2D()
      path.rect(x, y, width, height)
      this.stroke(path)
    }
    clearRect(x, y, width, height) {
      core.ops.op_canvas_clear(this.canvas.id, x, y, width, height, this._state())
    }

    // Текст: шрифты те же, что у страницы (см. `src/canvas/text.rs`).
    _text(text, x, y, stroke) {
      core.ops.op_canvas_text(this.canvas.id, String(text), this.font, x, y, ALIGN[this.textAlign] ?? 0, BASELINE[this.textBaseline] ?? 0, this._state(), stroke ? this._strokeColor : this._fillColor, stroke)
    }
    fillText(text, x, y) {
      this._text(text, x, y, false)
    }
    strokeText(text, x, y) {
      this._text(text, x, y, true)
    }
    measureText(text) {
      const [width, ascent, descent] = new Float32Array(core.ops.op_canvas_measure(this.font, String(text)).buffer)
      return {
        width,
        actualBoundingBoxLeft: 0,
        actualBoundingBoxRight: width,
        actualBoundingBoxAscent: ascent,
        actualBoundingBoxDescent: descent,
        fontBoundingBoxAscent: ascent,
        fontBoundingBoxDescent: descent,
      }
    }

    // Пиксели.
    putImageData(image, x, y) {
      const pixels = image.data instanceof Uint8ClampedArray ? image.data : new Uint8ClampedArray(image.data)
      core.ops.op_canvas_put_image_data(this.canvas.id, Math.round(x), Math.round(y), image.width, image.height, bytes(pixels))
    }
    getImageData(x, y, width, height) {
      const pixels = core.ops.op_canvas_get_image_data(this.canvas.id, Math.round(x), Math.round(y), width, height)
      return new ImageData(new Uint8ClampedArray(pixels), width, height)
    }
    createImageData(width, height) {
      return typeof width === 'object' ? new ImageData(width.width, width.height) : new ImageData(width, height)
    }

    /**
     * Перенос картинки с другого холста. Три вида вызова, как у браузера: целиком, по месту и по месту с размером —
     * ими игра увеличивает портреты (`portraits.ts`) и кладёт мини-карту (`minimap.ts`).
     */
    drawImage(source, ...rest) {
      const id = source?.id ?? -1
      const width = source?.width ?? 0
      const height = source?.height ?? 0
      const [a, b, c, d, e, f, g, h] = rest
      const place = rest.length === 2 ? [0, 0, width, height, a, b, width, height] : rest.length === 4 ? [0, 0, width, height, a, b, c, d] : [a, b, c, d, e, f, g, h]
      core.ops.op_canvas_draw_image(this.canvas.id, id, ...place, this._state())
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

    /** Флаги заливки картинок WebGL (переворот и умножение на альфу) ведёт обёртка: у OpenGL их нет. */
    pixelStorei(name, value) {
      if (name === 0x9240) this._flip = !!value
      else if (name === 0x9241) this._premultiply = !!value
      else call(ops.pixelStorei, name, value)
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

    /**
     * Два вида вызова, как у WebGL: с размерами и массивом пикселей — или с источником-картинкой (`format, type,
     * source`): холстом (так в кадр ложится холст интерфейса) или `ImageData`.
     */
    texImage2D(target, level, internal, ...rest) {
      if (rest.length === 3) {
        const [format, type, source] = rest
        if (source instanceof CanvasElement) {
          core.ops.op_gl_tex_image_canvas(target, level, internal, format, type, source.id, !!this._premultiply, !!this._flip)
          return
        }
        if (source?.data) {
          core.ops.op_gl_tex_image_2d(target, level, internal, source.width, source.height, 0, format, type, bytes(source.data))
          return
        }
        throw new TypeError('texImage2D: источник — только холст или ImageData')
      }
      const [width, height, border, format, type, pixels] = rest
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
