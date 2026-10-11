import { expect, test } from 'bun:test'
import type { HudState } from '../src/game/hud'
import type { Command } from '../src/sim'
import { commandsOf, type Deal } from '../src/ui/commands'

test('сделка космопорта: ресурс, потом количество шагами в пределах запаса, кредитов и трюма', () => {
  const trade: HudState['trade'] = {
    port: 7,
    order: null,
    offers: [{ resource: 'metal', available: 130, price: 10 }],
    purchases: [{ resource: 'metal', price: 30, room: 200 }, { resource: 'fuel', price: 36, room: 0 }],
  }
  const state = { trade, credits: 1000, units: [], building: 'spaceport' } as unknown as HudState
  let deal: Deal | null = null
  const sent: Command[] = []
  const nothing = () => {}
  const actions = { send: (command: Command) => void sent.push(command), place: nothing, pave: nothing, route: nothing, serve: nothing, patrol: nothing, open: nothing, pick: (next: Deal | null) => void (deal = next) }
  const grid = (page: 'sell' | 'buy') => commandsOf(state, page, actions, deal)
  const press = (page: 'sell' | 'buy', label: string) => grid(page).find((slot) => slot?.label === label)!.run()

  // Продажа: начинается с полусотни, «+100» упирается в запас, отправка шлёт набранное и закрывает сделку.
  press('sell', 'Металл ×130')
  expect(deal).toEqual({ resource: 'metal', amount: 50 })
  press('sell', '+100')
  expect(deal!.amount).toBe(130)
  press('sell', '−10')
  expect(grid('sell')[8]).toMatchObject({ label: 'Продажа ×120', cost: 1200, gain: true })
  grid('sell')[8]!.run()
  expect(sent).toEqual([{ type: 'sell', port: 7, resource: 'metal', amount: 120 }])
  expect(deal).toBeNull()

  // Закупка: не больше, чем хватает кредитов; ресурс, которому нет места в трюме, не выбрать.
  expect(grid('buy')[1]).toMatchObject({ label: 'Топливо', disabled: true })
  press('buy', 'Металл')
  expect(deal).toEqual({ resource: 'metal', amount: 33 })
  expect(grid('buy')[0]).toMatchObject({ label: '+10', disabled: true })
  expect(grid('buy')[8]).toMatchObject({ label: 'Закупка ×33', cost: 990 })
})
