// Редактор сохранений на холсте — игра в режиме бога: тот же холст, но симуляция стоит, а вместо нижней панели —
// боковая (editorPanel.ts). Тумана нет; разведанное игроками сохраняется таким, каким было. Перенесено со страницы
// (src/ui/EditorView.tsx).

import { createGame, type Game } from '../../game/game'
import { stage } from '../../game/stage'
import { loadSave, storeSave } from '../../game/storage'
import { t } from '../../i18n'
import type { MapSettings } from '../../map/settings'
import { reloadLocal } from '../../net/connect'
import { decodeSave, encodeSave, readJson, type Sections } from '../../save/file'
import { Ghost, createSim, type Sim, type SimSave } from '../../sim'
import { erase, playersOf } from '../../sim/editor'
import { applyEdit } from '../../sim/editOps'
import { slotWorker, type Launch } from '../../ui/launch'
import { download } from '../files'
import { menuButton } from '../form'
import type { Ui } from '../ui'
import { COLORS } from '../widgets'
import { EDITOR_WIDTH, createEditorPanel, type EditorPanel } from './editorPanel'
import type { Screen, ScreenOverlay } from './screen'

type EditorLaunch = Extract<Launch, { kind: 'editor' }>

/** Откуда открыто сохранение: мир и разделы файла, чтобы записать его обратно с ними же (HOST сервера, имя). */
interface Opened {
  save: SimSave
  sections?: Sections
}

async function open(launch: EditorLaunch): Promise<Opened> {
  if ('slot' in launch) {
    const save = await loadSave(launch.slot.id)
    if (!save) throw new Error(t('menu.notSavedYet'))
    return { save }
  }
  return decodeSave(launch.file)
}

export function createEditor(launch: EditorLaunch, settings: MapSettings, overlay: ScreenOverlay, exit: () => void): Screen {
  let game: Game | null = null
  let sim: Sim | null = null
  let opened: Opened | null = null
  let panel: EditorPanel | null = null
  let error: unknown = null
  let status = ''
  let dirty = false
  /** «Выйти» с несохранённым нажато один раз: второе нажатие подтверждает. */
  let leaving = false
  let closed = false

  open(launch)
    .then((result) => {
      if (closed) return
      opened = result
      // Тумана в редакторе нет: видно всё, а не то, что разведал игрок.
      const world = createSim({ ...result.save, fog: false })
      // Симуляция стоит: кадры игры зовут advance, а тиков нет.
      const frozen: Sim = Object.create(world, { advance: { value: () => 0 } })
      sim = world
      game = createGame(stage(), settings, (reason) => (error = reason), { sim: frozen, player: playersOf(world)[0] ?? 1 }, { editor: true, overlay })
      panel = createEditorPanel({
        sim: world,
        game,
        apply: (edit) => applyEdit(world, edit),
        changed: () => {
          dirty = true
          status = ''
          leaving = false
        },
      })
    })
    .catch((reason: unknown) => (error = reason))

  const save = async () => {
    if (!sim || !opened) return
    // Призрак юнита под указателем — не часть мира.
    for (const [ghost] of sim.world.query(Ghost)) erase(sim, ghost)
    const { fog: _, ...rest } = sim.save()
    // Без тумана разведанной была бы вся карта: игроки получают прежнее разведанное.
    const result: SimSave = { ...rest, ...(opened.save.fog === false && { fog: false }), explored: opened.save.explored }
    try {
      if ('slot' in launch) {
        await storeSave(launch.slot.id, result)
        // Если в слот играют в другой вкладке, её мир — поправленный: иначе автосохранение игры затёрло бы правки.
        reloadLocal(slotWorker(launch.slot.id), result)
        status = t('editor.savedToSlot')
      } else {
        // Свои разделы файла (игроки сервера, имя слота) пишутся обратно как были.
        const extras: Record<string, unknown> = {}
        for (const tag of opened.sections?.keys() ?? []) {
          if (!['META', 'WRLD', 'LAND', 'EXPL'].includes(tag)) extras[tag] = readJson(opened.sections!.get(tag))
        }
        download(launch.name.endsWith('.kharos') ? launch.name : `${launch.name}.kharos`, await encodeSave(result, extras))
        status = t('editor.fileDownloaded')
      }
      dirty = false
    } catch (reason) {
      status = reason instanceof Error ? reason.message : String(reason)
    }
  }

  return {
    insets: () => ({ top: 0, bottom: 0, right: game ? EDITOR_WIDTH + 16 : 0 }),
    draw(ui: Ui) {
      if (error !== null) {
        ui.box({ anchor: { left: 0, right: 0, top: 0, bottom: 0 }, background: 'rgb(8 17 28 / 0.88)', direction: 'column', align: 'center', justify: 'center', gap: 12, padding: 24 }, () => {
          ui.text(t('editor.failed'))
          ui.text(error instanceof Error ? error.message : String(error), { color: COLORS.soft, wrap: Math.min(720, ui.width - 48) })
          if (menuButton(ui, 'editor:menu', t('common.toMenu'))) exit()
        })
        return
      }
      if (!panel) return
      const name = 'slot' in launch ? launch.slot.name : launch.name
      panel.draw(ui, t('editor.title'), `${name}${status ? ` · ${status}` : ''}`, 8, () => {
        if (menuButton(ui, 'editor:exit', leaving ? `${t('editor.exit')}?` : t('editor.exit'), { tip: leaving ? t('editor.exitConfirm') : undefined, danger: leaving })) {
          if (dirty && !leaving) leaving = true
          else exit()
        }
        if (menuButton(ui, 'editor:save', `${'slot' in launch ? t('editor.save') : t('editor.downloadFile')}${dirty ? ' •' : ''}`, { primary: true })) void save()
      })
    },
    destroy() {
      closed = true
      panel?.destroy()
      game?.destroy()
    },
  }
}
