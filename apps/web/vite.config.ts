import { defineConfig } from 'vite'

export default defineConfig({
  // Относительные пути: клиент открывается и из корня домена, и из /kharos/ на GitHub Pages.
  base: './',
  build: {
    // Сборка — в корневой dist, как до монорепы: туда смотрят CI страниц и `vite preview`.
    outDir: '../../dist',
    emptyOutDir: true,
  },
})
