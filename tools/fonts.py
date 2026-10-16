"""Шрифты интерфейса: полный Ark Pixel (12 и 10 px) и GNU Unifont как запасной — для всего, чего в Ark Pixel нет
(ники пишут чем угодно). Оба пиксельные, оба под лицензией OFL.

Что делает:
- качает закреплённые версии в .cache/fonts (повторный запуск их не качает);
- режет шрифты для страницы на куски woff2 по диапазонам Юникода (public/fonts) и пишет к ним packages/client/src/fonts.css с
  `unicode-range`: браузер качает только куски, чьи буквы встретились, — латиница с кириллицей это 26 КБ, а иероглифы
  и Unifont приходят, лишь когда они понадобились;
- кладёт полные OTF для нативной оболочки (native/fonts): её холст пишет текст сам (`fontdue`), с диска.

Unifont рисует глифы в 16 точек — ровно высота строки Ark Pixel 12 (13 сверху, 3 снизу). Чтобы он оставался
пиксельным в стеке шрифтов с Ark Pixel 12px, его семейства увеличены через `size-adjust`: у «Kharos Unifont 12» кегль
12 даёт настоящие 16. Нативная оболочка знает те же семейства с тем же увеличением (см. native/src/canvas/text.rs).

Запуск: python3 tools/fonts.py (нужен fontTools с brotli: pip install fonttools brotli).
"""

import io
import shutil
import sys
import urllib.request
import zipfile
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / '.cache' / 'fonts'
PUBLIC = ROOT / 'public' / 'fonts'
NATIVE = ROOT / 'native' / 'fonts'
CSS = ROOT / 'packages' / 'client' / 'src' / 'fonts.css'

ARK = '2026.09.25'
UNIFONT = '18.0.01'

# Источники: файл в кэше, откуда он берётся и что из архива вынуть.
SOURCES = {
    'ark-12': (f'ark-pixel-12px-proportional-latin.otf', f'https://github.com/TakWolf/ark-pixel-font/releases/download/{ARK}/ark-pixel-font-12px-proportional-otf-v{ARK}.zip'),
    'ark-10': (f'ark-pixel-10px-proportional-latin.otf', f'https://github.com/TakWolf/ark-pixel-font/releases/download/{ARK}/ark-pixel-font-10px-proportional-otf-v{ARK}.zip'),
    'unifont': (f'unifont-{UNIFONT}.otf', f'https://unifoundry.com/pub/unifont/unifont-{UNIFONT}/font-builds/unifont-{UNIFONT}.otf'),
    'unifont-license': ('unifont-OFL.txt', 'https://unifoundry.com/OFL-1.1.txt'),
    'ark-license': ('OFL.txt', f'https://github.com/TakWolf/ark-pixel-font/releases/download/{ARK}/ark-pixel-font-10px-proportional-otf-v{ARK}.zip'),
}

# Сколько глифов в одном куске для страницы: латиница, кириллица и прочие алфавиты (до U+2E80) идут одним куском,
# иероглифы — кусками по столько.
CHUNK = 2500
# Граница «алфавитов»: дальше — иероглифы, слоги и прочее крупное.
SCRIPTS_END = 0x2E80


def fetch(key: str) -> Path:
    """Файл источника в кэше; нет — качается (из архива вынимается нужный файл)."""
    name, url = SOURCES[key]
    path = CACHE / name
    if path.exists():
        return path
    CACHE.mkdir(parents=True, exist_ok=True)
    print(f'качаю {url}')
    with urllib.request.urlopen(url) as response:
        data = response.read()
    if url.endswith('.zip'):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            data = archive.read(name)
    path.write_bytes(data)
    return path


def ranges(codes: list[int], blocked: set[int]) -> str:
    """`unicode-range` куска: коды, слитые в отрезки. Дыру между кодами можно перешагнуть, если в ней нет кодов из
    blocked: у Unifont это буквы Ark Pixel. Иначе `document.fonts.load` качал бы кусок Unifont ради букв, которые
    рисует Ark, — он берёт все семейства стека, чьи диапазоны задевает текст. Поштучно же список у Unifont без букв
    Ark вышел бы на сотню килобайт."""
    parts = []
    start = previous = codes[0]
    for code in codes[1:] + [None]:
        if code is not None and not any(gap in blocked for gap in range(previous + 1, code)):
            previous = code
            continue
        parts.append(f'U+{start:X}' if start == previous else f'U+{start:X}-{previous:X}')
        if code is not None:
            start = previous = code
    return ', '.join(parts)


def cut(source: Path, codes: list[int], out: Path) -> None:
    """Кусок шрифта с нужными кодами — в woff2."""
    options = subset.Options()
    options.flavor = 'woff2'
    options.layout_features = ['*']
    options.name_IDs = ['*']
    options.notdef_outline = True
    font = TTFont(source)
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=codes)
    subsetter.subset(font)
    font.flavor = 'woff2'
    font.save(out)


def faces(source: Path, prefix: str, families: list[tuple[str, str]], skip: set[int] = frozenset()) -> list[str]:
    """Режет шрифт на куски и возвращает правила @font-face: по правилу на кусок и семейство."""
    codes = sorted(code for code in TTFont(source).getBestCmap() if code not in skip)
    scripts = [code for code in codes if code < SCRIPTS_END]
    rest = [code for code in codes if code >= SCRIPTS_END]
    chunks = [scripts] + [rest[i : i + CHUNK] for i in range(0, len(rest), CHUNK)]
    rules = []
    total = 0
    for index, chunk in enumerate(chunk for chunk in chunks if chunk):
        out = PUBLIC / f'{prefix}-{index}.woff2'
        cut(source, chunk, out)
        total += out.stat().st_size
        for family, extra in families:
            rules.append(
                '@font-face {\n'
                f'  font-family: "{family}";\n'
                f'  src: url("/fonts/{out.name}") format("woff2");\n'
                '  font-display: block;\n'
                f'{extra}'
                f'  unicode-range: {ranges(chunk, skip)};\n'
                '}\n'
            )
    print(f'{prefix}: {len(codes)} глифов, кусков {len(chunks)}, {total // 1024} КБ')
    return rules


def main() -> None:
    ark12, ark10, unifont = fetch('ark-12'), fetch('ark-10'), fetch('unifont')
    PUBLIC.mkdir(parents=True, exist_ok=True)
    for old in PUBLIC.glob('*.woff2'):
        old.unlink()
    shutil.copy(fetch('ark-license'), PUBLIC / 'OFL.txt')
    # В тексте лицензии с сайта Unifont нет строки о правах — она берётся из самого шрифта.
    copyright = TTFont(unifont)['name'].getDebugName(0)
    (PUBLIC / 'unifont-OFL.txt').write_text(f'GNU Unifont {UNIFONT}\nhttps://unifoundry.com/unifont/\n\n{copyright}\n\n' + fetch('unifont-license').read_text())

    rules = []
    rules += faces(ark12, 'ark-pixel-12px', [('Ark Pixel 12', '')])
    rules += faces(ark10, 'ark-pixel-10px', [('Ark Pixel 10', '')])
    # Unifont нужен только там, где нет Ark Pixel 12: остальное он никогда не рисует.
    covered = set(TTFont(ark12).getBestCmap())
    rules += faces(
        unifont,
        'unifont',
        [
            ('Kharos Unifont 12', '  size-adjust: 133.333%;\n'),
            ('Kharos Unifont 10', '  size-adjust: 160%;\n'),
        ],
        covered,
    )
    header = (
        '/* Создано tools/fonts.py — руками не править. Ark Pixel и GNU Unifont, лицензия OFL (public/fonts).\n'
        ' * Куски по unicode-range: браузер качает только те, чьи буквы встретились на странице. */\n\n'
    )
    CSS.write_text(header + '\n'.join(rules))

    NATIVE.mkdir(parents=True, exist_ok=True)
    for source, name in ((ark12, 'ark-pixel-12px.otf'), (ark10, 'ark-pixel-10px.otf'), (unifont, 'unifont.otf')):
        shutil.copy(source, NATIVE / name)
    print(f'записано: {CSS.relative_to(ROOT)}, public/fonts, native/fonts')


if __name__ == '__main__':
    sys.exit(main())
