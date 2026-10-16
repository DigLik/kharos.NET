// Файлы сохранений: выбрать с диска и отдать на скачивание. На странице — через <input type="file"> и ссылку на Blob;
// у нативной оболочки ни того, ни другого пока нет (пункт 10.5 плана), и там выбор возвращает null, а скачивание
// ничего не делает.

/** Выбранный файл: имя и байты. */
export interface PickedFile {
  name: string
  bytes: Uint8Array
}

/** Открывает выбор файла с расширением accept (например, «.kharos»). Отмена или нет выбора — null. */
export function pickFile(accept: string): Promise<PickedFile | null> {
  const input = document.createElement('input') as HTMLInputElement
  if (!('files' in input)) return Promise.resolve(null)
  return new Promise((resolve) => {
    input.type = 'file'
    input.accept = accept
    input.onchange = async () => {
      const file = input.files?.[0]
      resolve(file ? { name: file.name, bytes: new Uint8Array(await file.arrayBuffer()) } : null)
    }
    input.click()
  })
}

/** Отдаёт браузеру файл на скачивание. */
export function download(name: string, data: Uint8Array) {
  if (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') return
  const link = document.createElement('a') as HTMLAnchorElement
  link.href = URL.createObjectURL(new Blob([data as BlobPart], { type: 'application/octet-stream' }))
  link.download = name
  link.click()
  URL.revokeObjectURL(link.href)
}
