/**
 * Балансный харнесс: считает числа игры из описаний и проверяет их в симуляции на карте по умолчанию.
 *
 * Разделы:
 *   table   — боевые числа юнитов: цена, прочность, дальность и урон по классам брони;
 *   duel    — парные бои отрядов равной цены;
 *   economy — рудник, производство и разгон настоящими командами;
 *   siege   — штурм узла обороны с ремонтом и без;
 *   raid    — набег на экономику противника;
 *   research — компоненты и лестница исследований: сколько харита на карте, что даёт линия, когда открывается тема;
 *   movement — движение: строй, окружение цели, подъезд строителей и нагрузка; formation, surround, builders, load —
 *              его части по отдельности;
 *   crossing — армия через карту 512×512 по диагонали на нескольких картах: доезжают ли все. Долгий.
 *
 * Запуск: bun run balance            — таблица и парные бои (как раньше);
 *        bun run balance all        — все разделы;
 *        bun run balance economy    — только один раздел.
 */
import { printDuels, printStats, runCombat } from './harness/combat'
import { runEconomy } from './harness/economy'
import { runSiege } from './harness/siege'
import { runRaids } from './harness/raid'
import { runResearch } from './harness/research'
import { printBuilders, printCrossing, printFormation, printLoad, printSurround, runMovement } from './harness/movement'

const modes: Record<string, () => void> = {
  table: () => printStats(),
  duel: () => printDuels(),
  combat: () => runCombat(),
  economy: () => runEconomy(),
  siege: () => runSiege(),
  raid: () => runRaids(),
  research: () => runResearch(),
  movement: () => runMovement(),
  formation: () => printFormation(),
  surround: () => printSurround(),
  builders: () => printBuilders(),
  load: () => printLoad(),
  crossing: () => printCrossing(),
  all: () => {
    runCombat()
    runEconomy()
    runSiege()
    runRaids()
    runResearch()
    runMovement()
    printCrossing()
  },
}

const mode = process.argv[2] ?? 'combat'
const run = modes[mode]
if (!run) {
  console.error(`Неизвестный раздел «${mode}». Есть: ${Object.keys(modes).join(', ')}`)
  process.exit(1)
}
run()
