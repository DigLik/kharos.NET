/**
 * Сервер вживую: запускает собранный сервер (бандл или бинарник), подключает реплику, ждёт мир, приказывает своему
 * юниту ехать и проверяет, что он поехал. Сервер работает в .cache/smoke/server со своими настройками и
 * сохранением — настоящие не трогаются.
 * bun tools/smoke/server.ts <команда сервера...>, например: bun tools/smoke/server.ts bun .cache/smoke/out/kharos-server-bun.js
 */
import { spawn } from 'node:child_process'
import { mkdirSync, rmSync } from 'node:fs'
import { resolve } from 'node:path'
import { Owner, Position, Unit } from '@kharos/sim-ts/components'
import { decodeServer, PROTOCOL_VERSION } from '../../src/net/protocol'
import { createReplica, type Replica } from '../../src/net/replica'

const [command, ...args] = process.argv.slice(2)
if (!command) throw new Error('bun tools/smoke/server.ts <команда сервера...>')
const dir = new URL('../../.cache/smoke/server', import.meta.url).pathname
rmSync(dir, { recursive: true, force: true })
mkdirSync(dir, { recursive: true })
const PORT = 8799
// Пути из командной строки — от каталога запуска, а сервер работает в своём.
const local = (arg: string) => (arg.startsWith('.') ? resolve(arg) : arg)
const server = spawn(local(command), args.map(local), {
  cwd: dir,
  env: { ...process.env, PORT: String(PORT), SETTINGS: `${dir}/settings.json`, SAVE: `${dir}/save.kharos` },
})
let log = ''
server.stdout.on('data', (chunk: Buffer) => (log += chunk))
server.stderr.on('data', (chunk: Buffer) => (log += chunk))
const sleep = (ms: number) => new Promise((done) => setTimeout(done, ms))
const fail = (why: string): never => {
  console.log(`FAIL ${why}\n--- журнал сервера ---\n${log}`)
  server.kill()
  process.exit(1)
}
await sleep(1500)

const url = new URL(`ws://127.0.0.1:${PORT}/`)
url.searchParams.set('id', `smoke-${Date.now()}`)
url.searchParams.set('version', String(PROTOCOL_VERSION))
url.searchParams.set('name', 'smoke')
const socket = new WebSocket(url)
socket.binaryType = 'arraybuffer'
let replica: Replica | undefined
let player = 0
socket.onmessage = (event) => {
  const message = decodeServer(event.data)
  if (!replica) {
    if (message.type !== 'welcome') return
    player = message.player
    replica = createReplica(message, (text) => socket.send(text), () => socket.close())
    return
  }
  replica.receive(message)
  try {
    replica.advance(0)
  } catch {}
}
socket.onerror = () => fail('сокет не открылся')
for (let i = 0; i < 40 && !replica; i++) await sleep(250)
if (!replica) fail('нет welcome')
const sim = replica!
// Мир приходит снимком через несколько тиков после входа.
await sleep(4000)

const mine: number[] = []
for (const [entity, owner] of sim.world.query(Owner, Unit)) if (owner.player === player) mine.push(entity)
if (!mine.length) fail(`своих юнитов нет, сущностей в мире ${sim.world.size}`)
const unit = mine.find((entity) => sim.world.get(entity, Unit)!.type !== 'mcv') ?? mine[0]
const before = { ...sim.world.get(unit, Position)! }
sim.send(player, { type: 'move', units: [unit], x: Math.floor(before.x) + 6, y: Math.floor(before.y) })
await sleep(3000)
const after = sim.world.get(unit, Position)!
const moved = Math.hypot(after.x - before.x, after.y - before.y)
console.log(`игрок ${player}, своих юнитов ${mine.length}, сущностей ${sim.world.size}, юнит сдвинулся на ${moved.toFixed(2)}`)
socket.close()
server.kill()
if (moved < 0.5) fail('юнит не поехал')
console.log('OK server-live')
process.exit(0)
