import { defineConfig } from 'vite'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('.', import.meta.url))
const repo = fileURLToPath(new URL('../../../../', import.meta.url))

// Отдельная сборка страницы сверки кадров (Н6.4): клиент игры берётся как есть из packages/client, шрифты — из apps/web/public.
export default defineConfig({
  root,
  base: './',
  publicDir: `${repo}apps/web/public`,
  build: {
    outDir: `${repo}dotnet/.cache/frames-build`,
    emptyOutDir: true,
    target: 'esnext',
    chunkSizeWarningLimit: 4000,
  },
  server: { fs: { allow: [repo] } },
})
