// Подложка на аккордах меню игры (audio/music.ts): Dm — Bb — C — Am, пэд, бас, пентатоника сверху.
import { writeFileSync } from 'fs'
const SR = 44100, LEN = 62, CH = 4
const CHORDS = [[38, 57, 62, 65, 69], [34, 53, 58, 62, 65], [36, 55, 60, 64, 67], [33, 52, 57, 60, 64]]
const MEL = [74, 77, 79, 81, 84, 86]
const f = (n: number) => 440 * 2 ** ((n - 69) / 12)
const out = new Float32Array(SR * LEN * 2)
let seed = 7; const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647)
for (let i = 0; i < SR * LEN; i++) {
  const t = i / SR, c = Math.floor(t / CH) % 4, ct = t % CH
  const env = Math.min(1, ct / 0.6) * Math.min(1, (CH - ct) / 0.6)
  let l = 0, r = 0
  CHORDS[c].forEach((n, k) => {
    const fr = f(n), a = k === 0 ? 0.16 : 0.05
    const v = Math.sin(2 * Math.PI * fr * t) + 0.3 * Math.sin(2 * Math.PI * fr * 2.003 * t) + 0.15 * Math.sin(2 * Math.PI * fr * 3 * t)
    l += a * env * v * (k % 2 ? 0.8 : 1.1); r += a * env * v * (k % 2 ? 1.1 : 0.8)
  })
  out[i * 2] = l; out[i * 2 + 1] = r
}
// Мелодия: ноты со спадом, каждые 0.5–1.5 с.
for (let t = 1; t < LEN - 2; t += 0.5 + rnd()) {
  const fr = f(MEL[Math.floor(rnd() * MEL.length)]), pan = rnd()
  for (let j = 0; j < SR * 1.8; j++) {
    const i = Math.floor(t * SR) + j, tt = j / SR
    const v = 0.07 * Math.exp(-tt * 2.5) * Math.min(1, tt * 200) * (Math.sin(2 * Math.PI * fr * tt) + 0.2 * Math.sin(4 * Math.PI * fr * tt))
    out[i * 2] += v * (1 - pan); out[i * 2 + 1] += v * pan
  }
}
const buf = Buffer.alloc(44 + out.length * 2)
buf.write('RIFF', 0); buf.writeUInt32LE(36 + out.length * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16)
buf.writeUInt16LE(1, 20); buf.writeUInt16LE(2, 22); buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 4, 28); buf.writeUInt16LE(4, 32); buf.writeUInt16LE(16, 34)
buf.write('data', 36); buf.writeUInt32LE(out.length * 2, 40)
for (let i = 0; i < out.length; i++) buf.writeInt16LE(Math.round(Math.max(-1, Math.min(1, out[i] * 0.9)) * 32767), 44 + i * 2)
writeFileSync('music_raw.wav', buf)
