/**
 * Эталоны банка звуков base для тестов .NET (BankGolden.cs): записи набора TS (bank.ts, soundPieces) на 48 кГц —
 * левый канал, каждый stride-й отсчёт, длина и сумма модулей. Запуск из корня репозитория (после `bun install`):
 *   bun dotnet/tests/Kharos.Base.Tests/AudioTests/dump.ts dotnet/tests/Kharos.Base.Tests/AudioTests/BankGolden.cs <SoundBank.cs до Б-Зв>
 * Печатает строки словаря только для ключей, которых в BankGolden.cs ещё нет (прежние эталоны не трогает): у новых имён —
 * все варианты; у прежних имён без эталона — нулевой; у петель — нулевой. Ключ — имя записи банка мода и вариант.
 */
import { readFileSync } from 'node:fs'
import { SYNTH_RATE, soundPieces } from '../../../../packages/client/src/audio/bank'

const existing = new Set([...readFileSync(process.argv[2], 'utf8').matchAll(/\["([^"]+)"\] = new/g)].map((match) => match[1]))
const covered = new Set([...existing].map((key) => key.slice(0, key.lastIndexOf(':'))))
// Имена, что банк C# уже имел до Б-Зв: у них без эталона добавляется один вариант, у остальных (новых) — все.
const old = readFileSync(process.argv[3], 'utf8')
// Имя набора TS -> (имя в банке C#, вариант).
const convert = (key: string): [string, number] => {
  const parts = key.split(':')
  if (parts[0] === 'sound' || parts[0] === 'distant') return [parts[1], Number(parts[2])]
  if (parts[0] === 'loop') return [parts[1], 0]
  if (key === 'reverb:music') return ['', -1]
  return [key, 0]
}
const literal = (value: number) => {
  if (value === 0) return '0.0f'
  const text = String(value)
  if (!text.includes('e')) return `${text}f`
  const [mantissa, exponent] = text.split('e')
  return `${mantissa}e${exponent.replace('+', '')}f`
}
for (const [key, make] of soundPieces()) {
  const [name, variant] = convert(key)
  if (variant < 0 || existing.has(`${name}:${variant}`)) continue
  if (covered.has(name) || (variant > 0 && old.includes(`"${name}"`))) continue
  const data = make(SYNTH_RATE)[0]
  const stride = Math.floor(data.length / 64)
  const samples: string[] = []
  for (let i = 0; i < data.length; i += stride) samples.push(literal(data[i]))
  let sum = 0
  for (const value of data) sum += Math.abs(value)
  console.log(`        ["${name}:${variant}"] = new(${data.length}, ${sum.toPrecision(12)}, ${stride}, [${samples.join(', ')}]),`)
}
