// Настройки самого игрока на холсте: язык, звук, графика, интерфейс. Всё, что относится к миру (погода, размер,
// зерно), задаётся при создании игры. Окно одно и в главном меню, и в меню паузы.

import { CHANNELS, loadLevel, loadMuted, loadVolume, storeLevel, storeMuted, storeVolume } from '../../audio/audio'
import { MINIMAP_SIDES, SELECTION_LEVELS, STEPS_SHOWN, prefs, setPrefs } from '../../game/prefs'
import { LANGS, chooseLang, currentLang, t, type Key } from '@kharos/i18n'
import { FPS_LIMITS, PRESETS, RESOLUTIONS, graphics, presetOf, setGraphics, type Detail, type Preset } from '../../render/graphics'
import { checkbox, chips, groupTitle, menuWindow, row, slider } from '../form'
import type { Anchor, Ui } from '../ui'
import { COLORS } from '../widgets'

const DETAIL_NAMES = ['low', 'medium', 'high'] as const
const WIDTH = 300
/** Подпись поля слева. */
const LABEL = 88
/** Ширина содержимого окна. */
const INNER = WIDTH - 36

/**
 * Окно настроек. onSound — громкость или выключение звука поменялись: музыка меню и звук игры подстраиваются сразу.
 * Возвращает true, если нажали «Назад».
 */
export function settingsWindow(ui: Ui, onSound: () => void, anchor?: Anchor): boolean {
  return menuWindow(ui, { id: 'settings', title: t('common.settings'), width: WIDTH, back: t('common.back'), anchor }, () => {
    groupTitle(ui, t('menu.language'), true)
    const lang = chips(ui, 'settings:lang', LANGS.map(({ lang, name }) => [lang, name] as const), currentLang(), INNER)
    if (lang) void chooseLang(lang.picked)

    groupTitle(ui, t('menu.sound'))
    const muted = loadMuted()
    const sound = checkbox(ui, 'settings:muted', !muted, t('menu.soundAndMusic'))
    if (sound === muted) {
      storeMuted(!sound)
      onSound()
    }
    const volume = loadVolume()
    row(ui, t('menu.master'), LABEL, () => {
      const next = slider(ui, 'settings:volume', volume, { min: 0, max: 1, step: 0.05, width: INNER - LABEL - 48 - 16, disabled: muted })
      if (next !== volume) {
        storeVolume(next)
        onSound()
      }
      ui.box({ width: 40, direction: 'row', justify: 'end' }, () => ui.text(`${Math.round(volume * 100)}%`))
    })
    // Громкость групп — доля общей.
    for (const { channel } of CHANNELS) {
      const level = loadLevel(channel)
      row(ui, t(`menu.channels.${channel}`), LABEL, () => {
        const next = slider(ui, `settings:level:${channel}`, level, { min: 0, max: 1, step: 0.05, width: INNER - LABEL - 48 - 16, disabled: muted })
        if (next !== level) {
          storeLevel(channel, next)
          onSound()
        }
        ui.box({ width: 40, direction: 'row', justify: 'end' }, () => ui.text(`${Math.round(level * 100)}%`))
      })
    }

    graphicsSettings(ui)
    interfaceSettings(ui)
  })
}

/** Выбор из вариантов с подписью сверху (узкое окно): .menu__field--choice у страницы. */
function choice<T>(ui: Ui, id: string, label: string, tip: string, values: readonly T[], value: T, name: (value: T) => string): T | undefined {
  ui.box({ id: `${id}:label` }, () => ui.text(label, { color: COLORS.dim }))
  ui.tip(`${id}:label`, tip)
  return chips(ui, id, values.map((option) => [option, name(option)] as const), value, INNER, { fill: true })?.picked
}

/** Графика: набор целиком или каждый эффект отдельно. Эффекты не выключаются — упрощаются; смена видна сразу. */
function graphicsSettings(ui: Ui) {
  const value = graphics()
  const preset = presetOf(value)
  groupTitle(ui, t('menu.graphics'))
  ui.text(t('menu.quality'), { color: COLORS.dim })
  const presets = (Object.keys(PRESETS) as Preset[]).map((name) => [name, t(`menu.qualities.${name}`)] as const)
  const picked = chips(ui, 'settings:preset', preset ? presets : [...presets, [null, t('menu.qualities.custom')] as const], preset ?? null, INNER, { fill: true })
  if (picked?.picked) setGraphics(PRESETS[picked.picked])
  const resolution = choice(ui, 'settings:resolution', t('menu.resolution'), t('menu.resolutionTip'), RESOLUTIONS, value.resolution, (share) => `${share * 100}%`)
  if (resolution !== undefined) setGraphics({ ...value, resolution })
  const detail = (key: 'terrain' | 'shadows' | 'weather', label: Key, tip: Key) => {
    const level = choice<Detail>(ui, `settings:${key}`, t(label), t(tip), [0, 1, 2], value[key], (level) => t(`menu.levels.${DETAIL_NAMES[level]}`))
    if (level !== undefined) setGraphics({ ...graphics(), [key]: level })
  }
  detail('terrain', 'menu.terrainDetail', 'menu.terrainTip')
  detail('shadows', 'menu.shadows', 'menu.shadowsTip')
  detail('weather', 'menu.weatherDetail', 'menu.weatherTip')
  const fps = choice(ui, 'settings:fps', t('menu.fps'), t('menu.fpsTip'), FPS_LIMITS, value.fps, (fps) => (fps ? String(fps) : t('menu.noLimit')))
  if (fps !== undefined) setGraphics({ ...graphics(), fps })
  ui.text(t('menu.graphicsNote'), { color: COLORS.muted, wrap: INNER })
}

/** Интерфейс: то, что дело вкуса, а не мощности устройства. Смена видна сразу. */
function interfaceSettings(ui: Ui) {
  const value = prefs()
  groupTitle(ui, t('menu.interface'))
  const selection = choice(ui, 'settings:selection', t('menu.selection'), t('menu.selectionTip'), SELECTION_LEVELS, value.selection, (share) => `${Math.round(share * 100)}%`)
  if (selection !== undefined) setPrefs({ ...prefs(), selection })
  const minimap = choice(ui, 'settings:minimap', t('menu.minimap'), t('menu.minimapTip'), MINIMAP_SIDES, value.minimap, (side) => String(side))
  if (minimap !== undefined) setPrefs({ ...prefs(), minimap })
  const steps = choice(ui, 'settings:steps', t('menu.steps'), t('menu.stepsTip'), STEPS_SHOWN, value.steps, (shown) => t(`menu.stepsShown.${shown}`))
  if (steps !== undefined) setPrefs({ ...prefs(), steps })
}
