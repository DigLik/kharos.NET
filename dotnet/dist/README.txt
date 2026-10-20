Kharos {VERSION} ({RID})
==========================================================================

[[DESKTOP]]
РУССКИЙ
-------
Kharos — стратегия в реальном времени. Игра самодостаточна: ставить .NET не нужно.

Запуск
  1. Распакуйте архив в любую папку (лучше без кириллицы и пробелов в пути).
  2. Запустите {EXE}.
  Одиночная игра, сетевая игра и редактор — в меню. Сохранения и настройки лежат в
  папке пользователя (переменная KHAROS_HOME переопределяет её).
[[WIN]]
Сборка без подписи
  Windows SmartScreen может написать «Windows защитила ваш компьютер» и «Неизвестный
  издатель». Нажмите «Подробнее» -> «Выполнить в любом случае». Скачанный файл можно
  проверить по сумме: в PowerShell выполните
      Get-FileHash {ARCHIVE} -Algorithm SHA256
  и сравните результат с {ARCHIVE}.sha256 или со строкой в SHA256SUMS.txt релиза.
  Нужна видеокарта с DirectX 12 или Vulkan; без неё включится программный режим (медленнее).
[[/WIN]]
[[LINUX]]
Требования
  - Vulkan: драйвер видеокарты или mesa-vulkan-drivers (lavapipe даёт программный режим);
  - X11 или XWayland (чистый Wayland без XWayland не поддерживается);
  - ALSA или PulseAudio/PipeWire (libasound2, libpulse);
  - glibc 2.35 или новее.
Запуск: ./{EXE}  (бит исполнения сохранён в архиве; иначе chmod +x {EXE}).
Ярлык: рядом лежат kharos.desktop и kharos.png; скопируйте их в
~/.local/share/applications и ~/.local/share/icons и впишите в Exec= полный путь к {EXE}.
Проверка суммы: sha256sum -c {ARCHIVE}.sha256 (или по SHA256SUMS.txt релиза).
Сборка без подписи.
[[/LINUX]]

ENGLISH
-------
Kharos is a real-time strategy game. The build is self-contained: no .NET install needed.

Running
  1. Extract the archive to any folder (preferably without spaces or non-ASCII characters).
  2. Run {EXE}.
  Single player, network play and the editor are in the menu. Saves and settings live in
  the user's home folder (the KHAROS_HOME variable overrides it).
[[WIN]]
Unsigned build
  Windows SmartScreen may show "Windows protected your PC" / "Unknown publisher".
  Click "More info" -> "Run anyway" (Подробнее -> Выполнить в любом случае). To verify the
  download, in PowerShell run
      Get-FileHash {ARCHIVE} -Algorithm SHA256
  and compare it with {ARCHIVE}.sha256 or the matching line of the release's SHA256SUMS.txt.
  You need a DirectX 12 or Vulkan GPU; without one a slower software mode is used.
[[/WIN]]
[[LINUX]]
Requirements
  - Vulkan: a GPU driver, or mesa-vulkan-drivers (lavapipe gives a software mode);
  - X11 or XWayland (pure Wayland without XWayland is not supported);
  - ALSA or PulseAudio/PipeWire (libasound2, libpulse);
  - glibc 2.35 or newer.
Run: ./{EXE}  (the executable bit is kept in the archive; otherwise chmod +x {EXE}).
Desktop entry: kharos.desktop and kharos.png are included; copy them to
~/.local/share/applications and ~/.local/share/icons and set Exec= to the full path of {EXE}.
Verify: sha256sum -c {ARCHIVE}.sha256 (or compare with the release's SHA256SUMS.txt).
The build is unsigned.
[[/LINUX]]
[[/DESKTOP]]
[[SERVER]]
РУССКИЙ
-------
Выделенный сервер Kharos. Самодостаточен: .NET ставить не нужно.

Запуск
      PORT=8787 ./{EXE}
  Порт по умолчанию 8787. Настройки — settings.json: файл создаётся при первом запуске со
  всеми умолчаниями (port, lang, password, admin, save, size, fog, generator, tls и др.).
  Переменные PORT, SAVE, SETTINGS сильнее файла. Ключи командной строки:
  --settings путь, --mods папка, --game партия, --version.
  Остановка — Ctrl+C или SIGTERM. Игроки подключаются из игры (меню -> сетевая игра).
Проверка суммы: sha256sum -c {ARCHIVE}.sha256 (или по SHA256SUMS.txt релиза).
Сборка без подписи.
[[LINUX]]
Требования: glibc 2.35 или новее; видеокарта серверу не нужна.
[[/LINUX]]

ENGLISH
-------
Dedicated Kharos server. Self-contained: no .NET install needed.

Running
      PORT=8787 ./{EXE}
  The default port is 8787. Settings are in settings.json: it is created on first start with
  every default (port, lang, password, admin, save, size, fog, generator, tls, ...).
  The PORT, SAVE and SETTINGS variables override the file. Options:
  --settings path, --mods folder, --game match, --version.
  Stop with Ctrl+C or SIGTERM. Players connect from the game (menu -> network game).
Verify: sha256sum -c {ARCHIVE}.sha256 (or compare with the release's SHA256SUMS.txt).
The build is unsigned.
[[LINUX]]
Requirements: glibc 2.35 or newer; the server needs no GPU.
[[/LINUX]]
[[/SERVER]]

Licences / Лицензии: LICENSES/
