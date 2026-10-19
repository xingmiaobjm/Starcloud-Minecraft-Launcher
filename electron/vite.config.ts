/// 纯网页调试用的 Vite 配置（electron-vite 之外的独立入口）。
/// `npm run dev:web` 起它 → 浏览器打开 http://localhost:5173/ → F12 DevTools。
/// 渲染层检测不到 Electron preload 桥时会自动切到内置 mock 数据（见 bridge.ts）。
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'path'

export default defineConfig({
  root: resolve(__dirname, 'src/renderer'),
  plugins: [react()],
  server: { port: 5173, strictPort: true },
  build: { outDir: resolve(__dirname, 'out/web'), emptyOutDir: true }
})
