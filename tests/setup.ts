import { loadLang } from '../src/i18n'

// Словарь догружается, а тесты читают строки сразу: исходный язык — до первого теста.
await loadLang('ru')
