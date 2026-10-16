# Моды сервера

Мод — папка с `init.luau`. Сервер загружает моды из `mods` в `settings.json`:

```json
{ "mods": ["mods/hello"] }
```

Код мода выполняется только на сервере (и в воркере одиночной игры), в песочнице: нет файлов, сети, часов,
`require` и загрузки чужого кода. Библиотеки Luau — `string`, `table`, `math`, `bit32`, `utf8`, `buffer`, `vector`,
`coroutine`, `debug`. У мода свой срок на событие и свой предел памяти; кто трижды не уложился, тот выключается.
`print` пишет в консоль сервера.

API — таблица `kharos`, описание — [types/kharos.d.luau](types/kharos.d.luau). Подсказки и проверку типов в
редакторе даёт [luau-lsp](https://github.com/JohnnyMorganz/luau-lsp): в VS Code — расширение «Luau Language
Server», а в его настройке `luau-lsp.types.definitionFiles` — файл `mods/types/kharos.d.luau`.

Пример — [hello/init.luau](hello/init.luau).
