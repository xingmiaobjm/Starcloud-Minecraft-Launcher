import React from 'react'
import ReactDOM from 'react-dom/client'
import { FluentProvider, webDarkTheme } from '@fluentui/react-components'
import App from './App'

ReactDOM.createRoot(document.getElementById('root') as HTMLElement).render(
  <React.StrictMode>
    <FluentProvider theme={webDarkTheme} style={{ height: '100vh' }}>
      <App />
    </FluentProvider>
  </React.StrictMode>
)
