import React from 'react'
import ReactDOM from 'react-dom/client'

import App from './App'
import { installMockBridgeIfNeeded } from './bridge'
import { resolveInitialTheme } from './theme'
import './winui.css'

// 浏览器里直接开页面调试（没有 Electron preload 桥）时装 mock
installMockBridgeIfNeeded()

// 先定主题再 render：否则第一帧会被合成为默认亮色（"闪一下白"）。
// 见 theme.ts 坑点 2 —— data-theme 必须在 React 挂载前写进 <html>。
void resolveInitialTheme().then((initial) => {
  ReactDOM.createRoot(document.getElementById('root') as HTMLElement).render(
    <React.StrictMode>
      <App initialTheme={initial} />
    </React.StrictMode>
  )
})
