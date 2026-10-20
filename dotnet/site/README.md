# Страница Kharos (dotnet/site)

Статическая страница без сборки: `index.html`, `style.css`, `site.js`, `favicon.png`, `fonts/` (Ark Pixel 12px, подмножество 0 с кириллицей, и `OFL.txt`). Открывается и с файловой системы.

- Русский и английский: ссылки `#ru` / `#en`; выбор запоминается в localStorage, иначе по языку браузера.
- Кнопки подсвечиваются по ОС (`navigator.userAgentData.platform`, запасной путь `userAgent`; macOS и телефоны без подсветки).
- Ссылки скачивания: `https://github.com/<владелец>/releases/latest/download/<имя>`. Владелец задан атрибутом `data-repo` на `<body>`; `site.js` собирает из него все ссылки. Статические запасные ссылки (`noscript`, подвал до запуска скрипта) содержат `DigLik/kharos.NET`.

## Выкладка (pages.yml)

Подставить репозиторий и выложить папку как есть:

```yaml
- run: sed -i 's#DigLik/kharos\.NET#${{ github.repository }}#g' dotnet/site/index.html
- uses: actions/upload-pages-artifact@v3
  with: { path: dotnet/site }
- uses: actions/deploy-pages@v4
```

`sed` по `index.html` заменяет и `data-repo`, и запасные ссылки. Выкладка только при слиянии в `main` (ворота Г4).

Шрифт Ark Pixel © 2021 TakWolf, SIL OFL 1.1 (`fonts/OFL.txt`).
