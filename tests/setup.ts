import { loadLang } from '@kharos/i18n'

// Словарь догружается, а тесты читают строки сразу: исходный язык — до первого теста.
await loadLang('ru')
