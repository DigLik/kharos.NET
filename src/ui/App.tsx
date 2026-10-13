import { useEffect, useState } from 'preact/hooks'
import { loadSounds } from '../audio/loadSounds'
import { loadArt } from '../game/loadArt'
import { DEFAULT_SETTINGS } from '../map/settings'
import { EditorView } from './EditorView'
import { GameView } from './GameView'
import { Loading } from './Loading'
import { launchFromAddress, recallLaunch, rememberLast, rememberLaunch, type Launch } from './launch'
import { Menu } from './Menu'
import { Showcase } from './Showcase'
import { TerrainTest, isTerrainTest } from './TerrainTest'
import { useLang } from './useLang'

/**
 * Страница: главное меню над слайдами симуляции или сама игра. Игра, заданная адресной строкой (?server,
 * ?battle, ?sandbox), открывается сразу, минуя меню; после перезагрузки вкладка возвращается в свою игру.
 */
export function App() {
  // Язык поменялся — вся страница перерисовывается с новыми строками.
  useLang()
  if (isTerrainTest()) return <TerrainTest />
  return <WithAssets />
}

/**
 * Доли общей работы загрузки: звук синтезируется примерно вдесятеро дольше, чем рисуется графика. Оба воркера идут
 * разом, и полоса показывает, сколько всего готово.
 */
const ART_SHARE = 0.1
const SOUND_SHARE = 0.9

/**
 * Пока воркеры рисуют графику и синтезируют звук — экран загрузки: без графики не открыть ни меню с витриной, ни игру,
 * а звук в игре должен быть готов к первому же нажатию.
 */
function WithAssets() {
  const [share, setShare] = useState(0)
  const [ready, setReady] = useState(false)
  useEffect(() => {
    let live = true
    let art = 0
    let sound = 0
    const show = () => live && setShare(art * ART_SHARE + sound * SOUND_SHARE)
    Promise.all([
      loadArt((value) => ((art = value), show())),
      loadSounds((value) => ((sound = value), show())),
    ]).then(() => live && setReady(true))
    return () => {
      live = false
    }
  }, [])
  return ready ? <Game /> : <Loading share={share} />
}

function Game() {
  const [launch, setLaunch] = useState<Launch | null>(() => launchFromAddress() ?? recallLaunch())

  // Перезагрузка страницы возвращает в ту же игру, а не в меню.
  useEffect(() => {
    rememberLaunch(launch)
    if (launch) rememberLast(launch)
  }, [launch])

  if (launch?.kind === 'editor') return <EditorView launch={launch} settings={DEFAULT_SETTINGS} exit={() => setLaunch(null)} />
  if (launch) {
    return (
      <GameView
        launch={launch}
        settings={DEFAULT_SETTINGS}
        exit={() => {
          // Из игры, открытой адресной строкой, меню возвращает без параметров: иначе перезагрузка снова откроет её.
          if (location.search) history.replaceState(null, '', location.pathname)
          setLaunch(null)
        }}
      />
    )
  }
  return (
    <main class="game">
      <Showcase settings={DEFAULT_SETTINGS} />
      <Menu play={setLaunch} />
    </main>
  )
}
