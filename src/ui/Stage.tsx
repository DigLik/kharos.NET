import { useLayoutEffect, useRef } from 'preact/hooks'
import { stage } from '../game/stage'

/**
 * Место холста игры на экране. Сам холст один на страницу (см. stage.ts) и переезжает сюда, пока экран открыт;
 * name — его класс на этом экране.
 */
export function Stage({ name }: { name: string }) {
  const holder = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const place = holder.current!
    const canvas = stage()
    canvas.className = name
    place.appendChild(canvas)
    return () => {
      // Холст уже мог переехать на следующий экран.
      if (canvas.parentNode === place) canvas.remove()
    }
  }, [])
  return <div ref={holder} class="stage" />
}
