import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()]
  },
  preload: {
    plugins: [externalizeDepsPlugin()]
  },
  renderer: {
    plugins: [react(),
      {
        // 打成 IIFE 后 HTML 里的入口标签要��普通 script（type=module 在 file:// 下会被 CORS 拦）
        name: 'iife-entry-tag',
        enforce: 'post',
        transformIndexHtml(html) {
          // defer：普通 script 在 head 里同步执行，此时 <div id="root"> 还没解析，
          // createRoot 会拿到 null（React #299）。defer 保持先后顺序且延到 DOM 之后。
          return html.replace(/<script type="module" crossorigin /g, '<script defer ')
        }
      }
    ],
    build: {
      // 安卓 WebView 用 file:// 加载 assets，ES module 会被 CORS 拦掉（白屏）。
      // 打成单文件 IIFE（不用 type=module）后，file:// 下就能正常加载。
      rollupOptions: {
        output: {
          format: 'iife',
          inlineDynamicImports: true,
          entryFileNames: 'assets/[name].js',
          assetFileNames: 'assets/[name]-[hash][extname]'
        }
      }
    }
  }
})
