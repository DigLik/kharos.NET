import { expect, test } from 'bun:test'
import { LANGS, hostText, loadLang, rich, t, textOf } from '../src/i18n'
import ru from '../src/i18n/ru.json'

/** Строки словаря по ключу через точку. */
const flatten = (dict: object, prefix = '', out: Record<string, string> = {}) => {
  for (const [key, value] of Object.entries(dict)) {
    if (typeof value === 'string') out[prefix + key] = value
    else flatten(value, `${prefix}${key}.`, out)
  }
  return out
}
/** Что подставляется в строку: имена без «:lower» — строчными язык пишет по своим правилам. */
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)(?::\w+)?\}/g)].map((match) => match[1]).sort()

test('словари: в каждом языке те же ключи и те же подстановки, что в русском', async () => {
  const source = flatten(ru)
  for (const { lang } of LANGS) {
    const dict = flatten((await import(`../src/i18n/${lang}.json`)).default)
    expect(Object.keys(dict).sort()).toEqual(Object.keys(source).sort())
    for (const key of Object.keys(source)) expect([key, placeholders(dict[key])]).toEqual([key, placeholders(source[key])])
  }
})

test('строки: подстановки, строчные, значки между кусками и сообщения хоста на языке игрока', async () => {
  expect(t('menu.hours', { hours: 2, minutes: 5 })).toBe('2 ч 5 мин')
  expect(t('hud.depositTip', { name: 'Металл' })).toBe('Месторождение: металл')
  const icon = { icon: true }
  expect(rich('hud.depositLeft', { icon, name: 'Нефть', left: 'осталось 5' })).toEqual([icon, ' ', 'Нефть', ': ', 'осталось 5'])
  const usage = hostText('usage', { command: '/msg <player> <text>' })
  expect(textOf(usage)).toBe('Как: /msg <игрок> <текст>')
  expect(textOf('как написал человек')).toBe('как написал человек')
  await loadLang('en')
  expect(textOf(usage)).toBe('Usage: /msg <player> <text>')
  expect(textOf(hostText('help', { command: '/list', name: 'list', help: '' }))).toBe('/list — who is in the game now')
  await loadLang('ru')
})
