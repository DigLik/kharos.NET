import { forgetContext } from '../gl'

let canvas: HTMLCanvasElement | null = null

/**
 * Холст игры — один на страницу, а с ним и один контекст WebGL: витрина меню, игра и редактор рисуют на нём по
 * очереди. Новый холст — это новый контекст, а с ним заново все атласы и шейдеры на видеокарте; прежний при этом
 * живёт до сборки мусора. На страницу холст ставит компонент Stage.
 */
export function stage(): HTMLCanvasElement {
  if (canvas) return canvas
  const made = (canvas = document.createElement('canvas'))
  // Контекст могут отобрать и между играми, когда рендера нет: без отмены события браузер его не вернёт.
  made.addEventListener('webglcontextlost', (event) => event.preventDefault())
  // Общие ресурсы прежнего контекста недействительны. Слушатель стоит первым: рендер игры создаёт свои уже после.
  made.addEventListener('webglcontextrestored', () => {
    const gl = made.getContext('webgl2')
    if (gl) forgetContext(gl)
  })
  return made
}
