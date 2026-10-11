import { useEffect, useState } from 'preact/hooks'
import { currentLang, onLang } from '../i18n'

/** Перерисовывает компонент, когда язык поменялся: всё под ним берёт строки заново. */
export function useLang() {
  const [lang, setLang] = useState(currentLang)
  useEffect(() => onLang(() => setLang(currentLang())), [])
  return lang
}
