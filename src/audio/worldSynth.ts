/**
 * Звуки зданий, пуска ракет, щитов, стройки и прочего мира, синтезированные в коде, как и synth.ts. Разовые — функции
 * (rate, variant) → сэмплы, петли — rate → сэмплы без шва.
 */
import { attack, bandpass, decay, highpass, loop, lowpass, pulse, random, render } from './synth'

/** Металл: звонкие неровные обертоны от основы base, гаснут за time; возвращает сэмпл от времени удара. */
function metal(base: number, time: number, seed: number) {
  const next = random(seed)
  const ratios = [1, 2.32 + next() * 0.2, 3.89 + next() * 0.3, 5.61 + next() * 0.4, 7.13 + next() * 0.5]
  return (t: number) => {
    let output = 0
    ratios.forEach((ratio, i) => (output += Math.sin(2 * Math.PI * base * ratio * t) * decay(t, time / (1 + i * 0.6)) / (1 + i * 0.7)))
    return output
  }
}

/** Лязг: удар металла о металл в момент at секунд, громкостью level. */
function clangAt(rate: number, at: number, base: number, level: number, seed: number) {
  const ring = metal(base, 0.25, seed)
  const thud = lowpass(rate, 220)
  return (t: number, n: number) => (t < at ? thud(0) : ring(t - at) * level + thud(n * decay(t - at, 0.02)) * level * 3)
}

/** Огнемёт: хлопок поджига и рёв струи — шум, который вспыхивает и гаснет, со светлым шипением сверху. */
function flame(rate: number, variant: number) {
  let t = 0
  const roar = lowpass(rate, () => 700 + 1500 * decay(t, 0.12))
  const hiss = highpass(rate, 3000)
  const flutter = random(variant + 601)
  let gust = 1
  return render(rate, 0.55, variant, (time, noise) => {
    t = time
    if (Math.floor(time * 60) !== Math.floor((time - 1 / rate) * 60)) gust = 0.7 + flutter() * 0.3
    const n = noise()
    return (roar(n) * 2.4 * gust + hiss(n) * 0.25) * attack(time, 0.02) * decay(time, 0.18)
  })
}

/** Зенитка: сухой хлопок выстрела и тугой «тумп» снаряда, как удар по барабану. */
function flak(rate: number, variant: number) {
  const crack = highpass(rate, 2200)
  const body = lowpass(rate, 500)
  let phase = 0
  return render(rate, 0.35, variant, (t, noise) => {
    const n = noise()
    phase += (2 * Math.PI * (90 + 140 * decay(t, 0.02))) / rate
    return crack(n) * decay(t, 0.006) * 0.8 + body(n) * decay(t, 0.05) * 1.6 + Math.sin(phase) * decay(t, 0.07) * 0.8
  })
}

/** Гаубица: удар ниже пушечного, раскат катится дольше и уходит эхом. */
function artillery(rate: number, variant: number) {
  const crack = highpass(rate, 1200)
  const rumble = lowpass(rate, 220)
  const echo = lowpass(rate, 400)
  let phase = 0
  return render(rate, 1.9, variant, (t, noise) => {
    const n = noise()
    phase += (2 * Math.PI * (30 + 50 * decay(t, 0.15))) / rate
    const tail = t > 0.35 ? echo(n) * decay(t - 0.35, 0.45) * 0.9 : echo(0)
    return crack(n) * decay(t, 0.015) * 0.9 + Math.sin(phase) * decay(t, 0.4) * 1 + rumble(n) * decay(t, 0.5) * 2.6 + tail
  })
}

/** Бомба: свист падения — тон сползает вниз и крепнет. */
function bombFall(rate: number, variant: number) {
  let phase = variant * 0.3
  const air = bandpass(rate, 1400, 3)
  return render(rate, 0.9, variant, (t, noise) => {
    phase += (2 * Math.PI * (1500 - 700 * (t / 0.9))) / rate
    const swell = attack(t, 0.5) * Math.min(1, (0.9 - t) / 0.05)
    return (Math.sin(phase) * 0.5 + air(noise()) * 0.8) * swell
  })
}

/** Пуля попала: короткий щелчок о броню, иногда со звоном рикошета. */
function ricochet(rate: number, variant: number) {
  const next = random(variant + 701)
  const whine = next() < 0.6
  const start = 2800 + next() * 1600
  const tick = highpass(rate, 2500)
  let phase = 0
  return render(rate, whine ? 0.3 : 0.06, variant, (t, noise) => {
    phase += (2 * Math.PI * start * (1 - 0.35 * Math.min(1, t / 0.3))) / rate
    return tick(noise()) * decay(t, 0.004) * 1.2 + (whine ? Math.sin(phase) * attack(t, 0.01) * decay(t, 0.08) * 0.35 : 0)
  })
}

/** Подбитая машина: скрежет и хруст смятого металла после взрыва, долгий звон обшивки. */
function wreck(rate: number, variant: number) {
  const next = random(variant + 801)
  const crunch = bandpass(rate, 900 + next() * 400, 2)
  const ring = metal(180 + next() * 120, 0.6, variant + 811)
  const rattle = highpass(rate, 1500)
  return render(rate, 1.4, variant, (t, noise) => {
    const n = noise()
    const grit = t < 0.5 ? crunch(n) * (0.5 + 0.5 * Math.sin(2 * Math.PI * 23 * t) ** 2) * decay(t, 0.15) * 2 : crunch(0)
    return grit + ring(t) * 0.5 + rattle(n) * (next() < 0.003 ? 1 : 0) * decay(t, 0.5) * 3
  })
}

/** Рушится здание: глухой обвал — рокот, удары падающих плит и сыплющийся щебень. */
function collapse(rate: number, variant: number) {
  const next = random(variant + 901)
  const rumble = lowpass(rate, 140)
  const debris = bandpass(rate, 700, 1.5)
  const grit = highpass(rate, 2500)
  const falls = Array.from({ length: 7 }, () => ({ at: 0.15 + next() * 1.6, level: 0.5 + next() * 0.5 }))
  const thuds = lowpass(rate, 300)
  return render(rate, 2.6, variant, (t, noise) => {
    const n = noise()
    let hits = 0
    for (const fall of falls) if (t >= fall.at) hits += decay(t - fall.at, 0.05) * fall.level
    const sprinkle = next() < 0.02 * decay(t, 0.9) ? 1 : 0
    return rumble(n) * attack(t, 0.05) * decay(t, 0.8) * 3 + thuds(n * hits) * 4 + debris(n) * decay(t, 0.6) * 0.6 + grit(sprinkle) * 0.8
  })
}

/**
 * Ядерный взрыв: оглушительный удар, огромный бас, который сам почти не слышно, а чувствуется, и раскаты — один за
 * другим, всё дальше. Через полсекунды приходит ударная волна — второй тупой удар и рёв ветра.
 */
function nuke(rate: number, variant: number) {
  let t = 0
  const body = lowpass(rate, () => 90 + 1600 * decay(t, 0.3))
  const wind = bandpass(rate, 450, 0.8)
  const rolls = lowpass(rate, 160)
  const next = random(variant + 1001)
  const thunders = Array.from({ length: 5 }, (_, i) => ({ at: 1.2 + i * 0.7 + next() * 0.6, level: 0.8 / (1 + i * 0.4) }))
  let phase = 0
  return render(rate, 6, variant, (time, noise) => {
    t = time
    const n = noise()
    phase += (2 * Math.PI * (24 + 30 * decay(time, 0.4))) / rate
    const blast = body(n) * attack(time, 0.003) * decay(time, 1.1) * 3 + Math.sin(phase) * decay(time, 1.4) * 1.3
    const wave = time > 0.55 ? (Math.sin(2 * Math.PI * 40 * (time - 0.55)) * decay(time - 0.55, 0.15) * 0.4 + wind(n) * decay(time - 0.55, 1.2) * 2.5) * attack(time - 0.55, 0.01) : wind(0)
    let rolling = 0
    for (const thunder of thunders) if (time >= thunder.at) rolling += thunder.level * attack(time - thunder.at, 0.2) * decay(time - thunder.at, 0.9)
    return blast + wave + rolls(n) * rolling * 3
  })
}

/** Створки шахты встали: тяжёлый замок — глухой удар и звон металла. */
function doorsLocked(rate: number, variant: number) {
  const clang = clangAt(rate, 0, 95, 1, variant + 1101)
  const second = clangAt(rate, 0.12, 140, 0.6, variant + 1111)
  return render(rate, 1.2, variant, (t, noise) => {
    const n = noise()
    return clang(t, n) + second(t, n)
  })
}

/**
 * Старт ракеты: зажигание — хлопок и треск, — двигатель выходит на полную тягу с рёвом, который рвётся и
 * трещит, и ракета уходит вверх: рёв темнеет и стихает.
 */
function liftoff(rate: number, variant: number) {
  let t = 0
  const roar = lowpass(rate, () => 300 + 2200 * Math.min(1, t / 0.6) * decay(Math.max(0, t - 1.5), 1.5))
  const crackle = highpass(rate, 2000)
  const deep = lowpass(rate, 80)
  const next = random(variant + 1201)
  let gate = 1
  let until = 0
  return render(rate, 5, variant, (time, noise) => {
    t = time
    const n = noise()
    if (time >= until) {
      gate = 0.3 + next() * 0.7
      until = time + 0.004 + next() * 0.02
    }
    const swell = attack(time, 0.4) * decay(Math.max(0, time - 1.2), 1.4)
    return (roar(n) * 2.6 + crackle(n) * gate * 0.35 + deep(n) * 3) * swell + crackle(n) * decay(time, 0.03) * 1.5
  })
}

/** Щит принял удар: всплеск — шум с резким скольжением вниз и дрожащий звон поля. */
function shieldHit(rate: number, variant: number) {
  let t = 0
  const sweep = lowpass(rate, () => 600 + 5000 * decay(t, 0.05))
  let phase = variant * 0.2
  return render(rate, 0.45, variant, (time, noise) => {
    t = time
    phase += (2 * Math.PI * (380 + 220 * decay(time, 0.06)) * (1 + 0.02 * Math.sin(2 * Math.PI * 35 * time))) / rate
    return sweep(noise()) * decay(time, 0.05) * 1.3 + Math.sin(phase) * attack(time, 0.004) * decay(time, 0.15) * 0.6
  })
}

/** Щит пробит: поле гаснет — гул падает вниз, рвётся треском и глохнет. */
function shieldDown(rate: number, variant: number) {
  const crackle = highpass(rate, 1500)
  const next = random(variant + 1301)
  let phase = 0
  let gate = 0
  let until = 0
  return render(rate, 1.4, variant, (t, noise) => {
    if (t >= until) {
      gate = next() < 0.5 ? next() : 0
      until = t + 0.005 + next() * 0.02
    }
    phase += (2 * Math.PI * (320 * decay(t, 0.5) + 30)) / rate
    const hum = (Math.sin(phase) + Math.sin(phase * 2) * 0.4) * decay(t, 0.45)
    return hum * 0.8 + crackle(noise()) * gate * decay(t, 0.35) * 0.9
  })
}

/** Щит поднялся: гул нарастает снизу вверх и встаёт на ровную ноту. */
function shieldUp(rate: number, variant: number) {
  let phase = 0
  return render(rate, 1, variant, (t) => {
    phase += (2 * Math.PI * (120 + 260 * Math.min(1, t / 0.6)) * (1 + variant * 0.01)) / rate
    return (Math.sin(phase) + Math.sin(phase * 2.01) * 0.35) * attack(t, 0.5) * Math.min(1, (1 - t) / 0.3)
  })
}

/** Ворота: шипит гидравлика, створка едет и встаёт с лязгом. */
function gate(rate: number, variant: number) {
  const hiss = bandpass(rate, 2500, 1)
  const whine = lowpass(rate, 400)
  const clang = clangAt(rate, 0.55, 120, 0.7, variant + 1401)
  return render(rate, 1, variant, (t, noise) => {
    const n = noise()
    const moving = t < 0.55 ? attack(t, 0.05) : 0
    const motor = Math.sin(2 * Math.PI * 110 * t) * 0.25 + whine(n) * 0.6
    return hiss(n) * moving * 0.6 + motor * moving + clang(t, n)
  })
}

/** Здание достроено: последний удар — затянутый болт, глухой удар и короткий звонкий аккорд металла. */
function built(rate: number, variant: number) {
  const ratchet = bandpass(rate, 3000, 4)
  const clang = clangAt(rate, 0.22, 160, 0.8, variant + 1501)
  return render(rate, 1, variant, (t, noise) => {
    const n = noise()
    const clicks = t < 0.2 ? pulse(t, 0.035, 0.003) : 0
    return ratchet(n * clicks) * 3 + clang(t, n)
  })
}

/**
 * MCV разворачивается: гидравлика воет и шипит, опоры встают с ударами, щёлкают замки — три с лишним секунды,
 * как и само разворачивание.
 */
function deploy(rate: number, variant: number) {
  const hiss = bandpass(rate, 2200, 1)
  const clangs = [0.5, 1.4, 2.2, 3].map((at, i) => clangAt(rate, at, 90 + i * 25, 0.7, variant + 1601 + i))
  let phase = 0
  return render(rate, 3.4, variant, (t, noise) => {
    const n = noise()
    phase += (2 * Math.PI * (180 + 90 * Math.sin(Math.PI * Math.min(1, t / 3)))) / rate
    const servo = (((phase / (2 * Math.PI)) % 1) * 2 - 1) * 0.12 * attack(t, 0.1) * Math.min(1, (3.2 - t) / 0.2)
    let output = servo + hiss(n) * pulse(t, 0.9, 0.25, 0.3) * 0.5
    for (const clang of clangs) output += clang(t, n)
    return output
  })
}

/** Машина выехала из завода: шипит воротина, лязгает и катится рольставня. */
function rollout(rate: number, variant: number) {
  const hiss = highpass(rate, 2000)
  const rattle = bandpass(rate, 1100, 3)
  const clang = clangAt(rate, 0.5, 130, 0.5, variant + 1701)
  return render(rate, 0.9, variant, (t, noise) => {
    const n = noise()
    const shutter = t < 0.5 ? pulse(t, 0.045, 0.006) * attack(t, 0.05) : 0
    return hiss(n) * decay(t, 0.08) * 0.8 + rattle(n * shutter) * 4 + clang(t, n)
  })
}

/** Груз упал на землю: глухой удар ящиков и стук. */
function crates(rate: number, variant: number) {
  const thud = lowpass(rate, 260)
  const wood = bandpass(rate, 600, 4)
  return render(rate, 0.5, variant, (t, noise) => {
    const n = noise()
    const hits = decay(t, 0.03) + (t > 0.09 ? decay(t - 0.09, 0.02) * 0.6 : 0)
    return thud(n * hits) * 4 + wood(n * hits) * 2
  })
}

/** Покрытие легло: плотный шлепок бетона и скрежет мастерка. */
function paved(rate: number, variant: number) {
  const slap = lowpass(rate, 400)
  const scrape = bandpass(rate, 2500, 2)
  return render(rate, 0.45, variant, (t, noise) => {
    const n = noise()
    return slap(n) * decay(t, 0.04) * 3 + scrape(n) * (t > 0.08 ? attack(t - 0.08, 0.05) * decay(t - 0.08, 0.1) : 0) * 0.8
  })
}

/** Радар: мягкий звон импульса, уходящий эхом. */
function radar(rate: number, variant: number) {
  let phase = 0
  return render(rate, 1.6, variant, (t) => {
    phase += (2 * Math.PI * 1320 * (1 - 0.03 * t) * (1 + variant * 0.008)) / rate
    const echo = t > 0.4 ? decay(t - 0.4, 0.3) * 0.3 : 0
    return Math.sin(phase) * (attack(t, 0.003) * decay(t, 0.15) + echo)
  })
}

/** Сеть перегружена: свет мигает — гул проседает и рвётся. */
function brownout(rate: number, variant: number) {
  let phase = 0
  return render(rate, 1.2, variant, (t) => {
    phase += (2 * Math.PI * (100 - 40 * Math.min(1, t / 0.8)) * (1 + variant * 0.015)) / rate
    const flicker = t < 0.6 ? 0.4 + 0.6 * (Math.sin(2 * Math.PI * 9 * t) > -0.2 ? 1 : 0) : 1
    return (Math.sin(phase) + Math.sign(Math.sin(phase)) * 0.25 + Math.sin(phase * 3) * 0.2) * flicker * decay(t, 0.45) * attack(t, 0.01)
  })
}

/** Тон-колокольчик для уведомлений: ноты notes по очереди через gap, каждая звенит ring секунд. */
function chime(rate: number, variant: number, notes: number[], gap: number, ring: number) {
  return render(rate, gap * (notes.length - 1) + ring * 3, variant, (t) => {
    let output = 0
    notes.forEach((note, i) => {
      const local = t - i * gap
      if (local < 0) return
      const frequency = note * (1 + variant * 0.003)
      output += (Math.sin(2 * Math.PI * frequency * local) + Math.sin(2 * Math.PI * frequency * 2.76 * local) * 0.2 * decay(local, ring / 3)) * attack(local, 0.004) * decay(local, ring)
    })
    return output
  })
}

/** Отсчёт перед ядерным ударом: короткий высокий писк. */
function beep(rate: number, variant: number) {
  let phase = 0
  return render(rate, 0.12, variant, (t) => {
    phase += (2 * Math.PI * 1760 * (1 + variant * 0.005)) / rate
    return (Math.sin(phase) * 0.8 + Math.sign(Math.sin(phase)) * 0.2) * attack(t, 0.003) * Math.min(1, (0.12 - t) / 0.02)
  })
}


/**
 * Шаг: удар подошвы — короткий глухой толчок — и шорох того, по чему ступили. grit — сколько хруста камешков,
 * soft — насколько глухо (песок, грязь), hard — звонкий щелчок по бетону, wet — чавканье болота.
 */
function step(rate: number, variant: number, surface: { grit: number; soft: number; hard: number; wet: number }) {
  const next = random(variant + 3001)
  const thud = lowpass(rate, 160 + next() * 60)
  const scuff = bandpass(rate, surface.soft ? 900 : 2200 + next() * 800, 1.2)
  const click = bandpass(rate, 3200 + next() * 600, 5)
  const slurp = bandpass(rate, 500, 3)
  const length = surface.soft ? 0.2 : 0.12
  return render(rate, length, variant, (t, noise) => {
    const n = noise()
    const crunch = next() < 0.06 * decay(t, 0.03) ? 1 : 0
    return (
      thud(n) * decay(t, 0.015) * 2.5 +
      scuff(n) * attack(t, 0.004) * decay(t, surface.soft ? 0.06 : 0.025) * (0.5 + surface.soft) +
      click(n) * decay(t, 0.004) * surface.hard * 2 +
      click(crunch) * surface.grit * 3 +
      slurp(n) * Math.sin(Math.PI * Math.min(1, t / length)) * surface.wet * 1.5
    )
  })
}

/** Лазер попал: шипение прожжённого металла — шум, который тает, с тонким звоном. */
function sizzle(rate: number, variant: number) {
  const hiss = highpass(rate, 3500)
  let phase = variant
  return render(rate, 0.35, variant, (t, noise) => {
    phase += (2 * Math.PI * 2600) / rate
    return hiss(noise()) * attack(t, 0.005) * decay(t, 0.1) * 1.2 + Math.sin(phase) * decay(t, 0.05) * 0.2
  })
}

/** Огонь лизнул цель: потрескивание горящего — щелчки поверх тихого гула пламени. */
function burn(rate: number, variant: number) {
  const next = random(variant + 3101)
  const roar = lowpass(rate, 500)
  const pop = bandpass(rate, 1800, 2)
  return render(rate, 0.5, variant, (t, noise) => {
    const n = noise()
    const crack = next() < 0.004 ? 1 : 0
    return roar(n) * decay(t, 0.2) * 1.4 + pop(crack) * 4 * decay(t, 0.3)
  })
}

/** Разряд ударил в цель: сухой щелчок и треск. */
function zap(rate: number, variant: number) {
  const snap = highpass(rate, 2000)
  const body = bandpass(rate, 700, 2)
  return render(rate, 0.18, variant, (t, noise) => {
    const n = noise()
    return snap(n) * decay(t, 0.008) * 1.5 + body(n) * decay(t, 0.03) * (0.6 + 0.4 * Math.sign(Math.sin(2 * Math.PI * 90 * t)))
  })
}

/** Корабль космопорта взлетает: прогрев, рёв двигателей нарастает и уходит вверх, светлея и стихая. */
function shipLaunch(rate: number, variant: number) {
  let t = 0
  const roar = lowpass(rate, () => 300 + 2500 * Math.min(1, t / 1.2))
  const deep = lowpass(rate, 90)
  let phase = 0
  return render(rate, 3.5, variant, (time, noise) => {
    t = time
    const n = noise()
    phase += (2 * Math.PI * (300 + 900 * Math.min(1, time / 1.5))) / rate
    const swell = attack(time, 1) * decay(Math.max(0, time - 1.3), 0.9)
    return (roar(n) * 2 + deep(n) * 2.5 + Math.sin(phase) * 0.12) * swell
  })
}

/** Корабль садится: рёв спускается сверху и тяжелеет, касание — глухой удар, шипит сброс давления. */
function shipLand(rate: number, variant: number) {
  let t = 0
  const roar = lowpass(rate, () => 2500 - 2100 * Math.min(1, t / 2))
  const deep = lowpass(rate, 90)
  const hiss = highpass(rate, 2500)
  const touch = lowpass(rate, 150)
  return render(rate, 3.5, variant, (time, noise) => {
    t = time
    const n = noise()
    const engine = (roar(n) * 2 + deep(n) * 2) * attack(time, 0.8) * (time < 2.2 ? 1 : decay(time - 2.2, 0.15))
    const thump = time > 2.2 ? touch(n) * decay(time - 2.2, 0.08) * 6 : touch(0)
    const vent = time > 2.4 ? hiss(n) * attack(time - 2.4, 0.03) * decay(time - 2.4, 0.35) * 0.8 : 0
    return engine + thump + vent
  })
}

/** Деньги пришли: звон монет — несколько ярких металлических ударов вразнобой. */
function coins(rate: number, variant: number) {
  const next = random(variant + 3201)
  const hits = Array.from({ length: 5 }, (_, i) => ({ at: i * 0.05 + next() * 0.03, ring: metal(2400 + next() * 900, 0.08, variant * 10 + i) }))
  return render(rate, 0.6, variant, (t) => {
    let output = 0
    for (const hit of hits) if (t >= hit.at) output += hit.ring(t - hit.at)
    return output
  })
}

/** Подарок: звонкий восходящий перебор. */
function gift(rate: number, variant: number) {
  return chime(rate, variant, [523, 659, 784, 1047, 1319], 0.06, 0.3)
}

/** Поражение: тяжёлый низкий аккорд, который сползает вниз и гаснет. */
function defeat(rate: number, variant: number) {
  const notes = [110, 130.8, 164.8]
  return render(rate, 4, variant, (t) => {
    const slide = (1 - 0.06 * Math.min(1, t / 3)) * (1 + variant * 0.006)
    let output = 0
    for (const note of notes) {
      const f = note * slide
      output += (Math.sin(2 * Math.PI * f * t) + Math.sin(2 * Math.PI * f * 2 * t) * 0.3 + Math.sin(2 * Math.PI * f * 3 * t) * 0.12) / notes.length
    }
    return output * attack(t, 0.15) * decay(t, 1.4)
  })
}

/** Капитуляция: долгий низкий гудок сирены — база гибнет волной. */
function surrender(rate: number, variant: number) {
  let phase = 0
  return render(rate, 3, variant, (t) => {
    phase += (2 * Math.PI * (150 + 40 * Math.sin(Math.PI * Math.min(1, t / 3))) * (1 + variant * 0.01)) / rate
    return (Math.sin(phase) + Math.sign(Math.sin(phase)) * 0.2 + Math.sin(phase * 1.5) * 0.3) * attack(t, 0.3) * Math.min(1, (3 - t) / 0.6)
  })
}

/** Месторождение выработано: бур сбегает с оборотов и глохнет, два низких тона. */
function depleted(rate: number, variant: number) {
  const grit = lowpass(rate, 900)
  let phase = 0
  return render(rate, 1.4, variant, (t, noise) => {
    phase += (2 * Math.PI * 95 * decay(t, 0.5)) / rate
    const drill = ((((phase / (2 * Math.PI)) % 1) * 2 - 1) * 0.4 + grit(noise())) * decay(t, 0.35)
    const tones = (t > 0.5 ? Math.sin(2 * Math.PI * 330 * t) * decay(t - 0.5, 0.15) : 0) + (t > 0.75 ? Math.sin(2 * Math.PI * 247 * t) * decay(t - 0.75, 0.25) : 0)
    return drill + tones * 0.5
  })
}

/** Рубильник: щелчок и гул, который уходит вниз (выключили) или поднимается (включили). */
function power(rate: number, variant: number, on: boolean) {
  const click = bandpass(rate, 1500, 3)
  let phase = 0
  return render(rate, 0.8, variant, (t, noise) => {
    const sweep = on ? Math.min(1, t / 0.5) : 1 - Math.min(1, t / 0.6)
    phase += (2 * Math.PI * (40 + 80 * sweep)) / rate
    const hum = (Math.sin(phase) + Math.sin(phase * 2) * 0.4) * (on ? attack(t, 0.1) * Math.min(1, (0.8 - t) / 0.3) : decay(t, 0.3))
    return click(noise()) * decay(t, 0.006) * 4 + hum * 0.6
  })
}

/** Кузов полон или пуст: глухой удар груза о металл. */
function cargo(rate: number, variant: number) {
  const thud = lowpass(rate, 220)
  const ring = metal(260 + variant * 20, 0.12, variant + 3301)
  return render(rate, 0.4, variant, (t, noise) => thud(noise()) * decay(t, 0.03) * 3 + ring(t) * 0.35)
}

/** Звонок чата: мягкий короткий тон; системное сообщение — ниже. */
function chat(rate: number, variant: number, note: number) {
  return chime(rate, variant, [note], 0.05, 0.12)
}

export const WORLD_SOUNDS = {
  flame,
  flak,
  artillery,
  bombFall,
  ricochet,
  wreck,
  collapse,
  nukeBlast: nuke,
  doorsLocked,
  liftoff,
  shieldHit,
  shieldDown,
  shieldUp,
  gate,
  built,
  deploy,
  rollout,
  crates,
  paved,
  radar,
  brownout,
  countdown: beep,
  /** Уведомления: машина готова, изучено, улучшено, боеголовка готова, награда. */
  unitReady: (rate: number, variant: number) => chime(rate, variant, [660, 880], 0.08, 0.2),
  researched: (rate: number, variant: number) => chime(rate, variant, [523, 659, 784, 1047], 0.09, 0.35),
  upgraded: (rate: number, variant: number) => chime(rate, variant, [587, 880], 0.1, 0.3),
  warheadReady: (rate: number, variant: number) => chime(rate, variant, [440, 415, 440], 0.16, 0.4),
  reward: (rate: number, variant: number) => chime(rate, variant, [784, 988, 1175], 0.07, 0.3),
  delivered: (rate: number, variant: number) => chime(rate, variant, [659, 784, 659], 0.08, 0.25),
  protectionOver: (rate: number, variant: number) => chime(rate, variant, [880, 659, 523], 0.14, 0.3),
  /** Шаги: по скале — хрустят камешки, по песку — шуршат, по болоту — чавкают, по бетону — стучат. */
  step: (rate: number, variant: number) => step(rate, variant, { grit: 1, soft: 0, hard: 0.2, wet: 0 }),
  stepSand: (rate: number, variant: number) => step(rate, variant, { grit: 0.2, soft: 1, hard: 0, wet: 0 }),
  stepMud: (rate: number, variant: number) => step(rate, variant, { grit: 0, soft: 0.6, hard: 0, wet: 1 }),
  stepHard: (rate: number, variant: number) => step(rate, variant, { grit: 0.3, soft: 0, hard: 1, wet: 0 }),
  sizzle,
  burn,
  zap,
  shipLaunch,
  shipLand,
  coins,
  gift,
  defeat,
  surrender,
  depleted,
  powerOn: (rate: number, variant: number) => power(rate, variant, true),
  powerOff: (rate: number, variant: number) => power(rate, variant, false),
  cargo,
  chat: (rate: number, variant: number) => chat(rate, variant, 1175),
  chatSystem: (rate: number, variant: number) => chat(rate, variant, 784),
} satisfies Record<string, (rate: number, variant: number) => Float32Array>

/** Перегрузка электростанции: треск дуг — шум, рвущийся на вспышки, — поверх надсадного гудения сети с перегрузом. */
function overload(rate: number) {
  const next = random(2001)
  const crackle = highpass(rate, 1800)
  const snap = bandpass(rate, 900, 2)
  let gate = 0
  let until = 0
  return loop(rate, 3, (t, noise) => {
    if (t >= until) {
      // Разряды идут пачками: то густо, то пусто.
      const burst = Math.sin(2 * Math.PI * 0.7 * t) > -0.3
      gate = burst && next() < 0.45 ? 0.3 + next() * 0.7 : 0
      until = t + 0.002 + next() * 0.01
    }
    const n = noise()
    const strain = Math.sign(Math.sin(2 * Math.PI * 100 * t)) * 0.12 + Math.sin(2 * Math.PI * 150 * t) * 0.1
    return crackle(n) * gate * 1.3 + snap(n) * gate * 0.8 + strain * (0.8 + 0.2 * Math.sin(2 * Math.PI * 3 * t))
  })
}

/** Стройка: удары молота, трещотка гайковёрта и шипение сварки — вразнобой. */
function construct(rate: number) {
  const hammer = clangAt(rate, 0, 380, 0.5, 2101)
  const ratchet = bandpass(rate, 3200, 5)
  const weld = highpass(rate, 3000)
  return loop(rate, 4, (t, noise) => {
    const n = noise()
    const knock = hammer(((t % 0.7) + 0.7) % 0.7, n) * (t % 2.8 < 2.1 ? 1 : 0)
    const drive = t > 2.2 && t < 3.2 ? ratchet(n * pulse(t, 0.03, 0.002)) * 2 : ratchet(0)
    const spark = weld(n) * (0.2 + 0.2 * Math.sin(2 * Math.PI * 0.6 * t)) * 0.4
    return knock + drive + spark
  })
}

/** Сварка ремонта: треск дуги — рваное шипение — и гул аппарата. */
function weld(rate: number) {
  const next = random(2201)
  const hiss = highpass(rate, 2500)
  let level = 0
  let until = 0
  return loop(rate, 2, (t, noise) => {
    if (t >= until) {
      level = 0.4 + next() * 0.6
      until = t + 0.01 + next() * 0.05
    }
    return hiss(noise()) * level + Math.sign(Math.sin(2 * Math.PI * 100 * t)) * 0.08
  })
}

/** Луч грузовика: переливчатый гул, который дышит, — груз течёт по лучу. */
function tractor(rate: number) {
  return loop(rate, 2, (t) => {
    const wobble = Math.sin(2 * Math.PI * 3 * t)
    return (Math.sin(2 * Math.PI * 220 * t + wobble * 2) * 0.6 + Math.sin(2 * Math.PI * 331 * t + wobble) * 0.3 + Math.sin(2 * Math.PI * 660 * t) * 0.1) * (0.7 + 0.3 * Math.sin(2 * Math.PI * 1.5 * t))
  })
}

/** Привод башни: тонкое жужжание мотора с редуктором. */
function servo(rate: number) {
  const gear = bandpass(rate, 1800, 6)
  return loop(rate, 2, (t, noise) => {
    const phase = (t * 240) % 1
    return (phase * 2 - 1) * 0.4 + gear(noise()) * 0.6 + Math.sin(2 * Math.PI * 480 * t) * 0.15
  })
}

/** Реактор: глубокий мерный пульс насосов, гул и шипение пара. */
function reactor(rate: number) {
  const steam = highpass(rate, 1500)
  const flow = lowpass(rate, 200)
  return loop(rate, 4, (t, noise) => {
    const n = noise()
    const pump = pulse(t, 1, 0.25) * Math.sin(2 * Math.PI * 45 * t)
    return pump * 0.8 + Math.sin(2 * Math.PI * 60 * t) * 0.25 + flow(n) * 0.8 + steam(n) * 0.08 * (1 + Math.sin(2 * Math.PI * 0.25 * t))
  })
}

/** Купол щита: ровный высокий гул поля с медленными биениями. */
function dome(rate: number) {
  return loop(rate, 4, (t) => (Math.sin(2 * Math.PI * 330 * t) + Math.sin(2 * Math.PI * 330.5 * t) + Math.sin(2 * Math.PI * 660.25 * t) * 0.3) * 0.5)
}

/** Гидравлика: створки шахты расходятся — ровный вой насоса и шипение. */
function hydraulic(rate: number) {
  const hiss = bandpass(rate, 2200, 1.5)
  return loop(rate, 2, (t, noise) => {
    const phase = (t * 140) % 1
    return (phase * 2 - 1) * 0.35 + Math.sin(2 * Math.PI * 280 * t) * 0.2 + hiss(noise()) * 0.5
  })
}

/** Пар из шахты: широкое шипение, пульсирующее клубами. */
function steam(rate: number) {
  const hiss = highpass(rate, 800)
  const body = lowpass(rate, 3000)
  return loop(rate, 3, (t, noise) => body(hiss(noise())) * (0.6 + 0.4 * Math.sin(2 * Math.PI * (2 / 3) * t) ** 2))
}

/** Ракетный двигатель: рёв струи с треском — для разгона в шахте и для ракеты в полёте. */
function rocket(rate: number) {
  const next = random(2301)
  const roar = lowpass(rate, 1400)
  const deep = lowpass(rate, 90)
  const crackle = highpass(rate, 2500)
  let gate = 0
  let until = 0
  return loop(rate, 3, (t, noise) => {
    if (t >= until) {
      gate = next() < 0.5 ? 0.3 + next() * 0.7 : 0
      until = t + 0.003 + next() * 0.015
    }
    const n = noise()
    return roar(n) * 2 + deep(n) * 3 + crackle(n) * gate * 0.4
  })
}

/** Накопитель заряжается или отдаёт: тонкий высокий свист преобразователя. */
function charge(rate: number) {
  return loop(rate, 2, (t) => Math.sin(2 * Math.PI * 3200 * t) * 0.3 + Math.sin(2 * Math.PI * 1600 * t) * 0.3 * (0.6 + 0.4 * Math.sin(2 * Math.PI * 2 * t)))
}

/** Трубы: груз течёт — глухое журчание и шорох в металле. */
function flow(rate: number) {
  const gurgle = bandpass(rate, 350, 4)
  const rush = lowpass(rate, 700)
  const swirl = random(2401)
  let level = 0.5
  let until = 0
  return loop(rate, 3, (t, noise) => {
    if (t >= until) {
      level = 0.3 + swirl() * 0.7
      until = t + 0.03 + swirl() * 0.08
    }
    const n = noise()
    return gurgle(n) * level * 2 + rush(n) * 0.8
  })
}

/** Петли мира: громкость каждой задаёт worldSounds.ts в game. */
export const WORLD_LOOPS = { overload, construct, weld, tractor, servo, reactor, dome, hydraulic, steam, rocket, charge, flow } satisfies Record<string, (rate: number) => Float32Array>
