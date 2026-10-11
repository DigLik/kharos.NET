import { render } from 'preact'
import { loadLang, preferredLang } from './i18n'
import { App } from './ui/App'
import { installSkin } from './ui/skin'

const rootElement = document.querySelector<HTMLDivElement>('#root')

if (!rootElement) {
  throw new Error('Root element was not found')
}

installSkin()
// Словарь догружается: до него рисовать нечем.
await loadLang(preferredLang())
render(<App />, rootElement)
