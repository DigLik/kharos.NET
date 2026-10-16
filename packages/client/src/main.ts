import { startApp } from './gui/app'
import { loadLang, preferredLang } from '@kharos/i18n'
import { installSkin } from './ui/skin'

const rootElement = document.querySelector<HTMLDivElement>('#root')

if (!rootElement) {
  throw new Error('Root element was not found')
}

installSkin()
// Словарь догружается: до него рисовать нечем.
await loadLang(preferredLang())
startApp(rootElement)
