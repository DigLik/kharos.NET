// Сборка нативной игры: тот же код, что у браузерной, тем же сборщиком — Vite знает и перевод шейдеров строкой
// (`?raw`), и воркеры. Отличий от браузерной сборки два: выход — файлы-модули (их читает окно, см. src/main.rs) и
// код не сжимается — по нему видно, что именно пошло в рантайм. Входов три: сама игра, проверка интерфейса
// (см. `js/ui.ts`) и интерфейс в окне (см. `js/hud.ts`); проверки окно берёт переменной окружения.
import { defineConfig } from 'vite'

export default defineConfig({
  // Отсчёт путей — от этого файла: сборку зовут и из корня игры, и из каталога окна.
  root: import.meta.dirname,
  build: {
    lib: {
      entry: { game: 'js/game.ts', ui: 'js/ui.ts', hud: 'js/hud.ts' },
      formats: ['es'],
      fileName: (_format, name) => `${name}.bundle.js`,
    },
    // Собирается рядом с точкой входа, а не в общем `dist`: у окна своя сборка.
    outDir: 'js',
    // Общий код двух входов — один файл с постоянным именем: окно читает сборку с диска, и хеш в имени копил бы рядом
    // мусор от прошлых сборок.
    rollupOptions: { output: { chunkFileNames: 'chunk-[name].js' } },
    emptyOutDir: false,
    target: 'esnext',
    minify: false,
    sourcemap: false,
  },
})
