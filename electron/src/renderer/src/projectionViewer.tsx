/// 投影预览：复用 Windows 端的 three.js + deepslate 渲染器
/// （Assets/Lab/projection-viewer.html，已随仓库复制到 public/lab/）。
///
/// 数据协议与 Win 端 WebView2 完全一致（voxelBegin → voxelStates/Models/Textures
/// → voxelBlocks → voxelEnd），资源由桥接 `lab.assets` 从客户端 jar 里抽。
/// 这样出来的画面不是"看起来像"，而是同一个渲染器、同一份资源。
import { useEffect, useRef, useState } from 'react'

import { request } from './bridge'

interface AssetsPayload {
  sizeX: number
  sizeY: number
  sizeZ: number
  truncated: boolean
  assetSource?: string
  blocks: Array<[number, number, number, string]>
  states: Record<string, string>
  models: Record<string, string>
  textures: Record<string, string>
}

function chunks<T>(items: T[], size: number): T[][] {
  const out: T[][] = []
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size))
  return out
}

export default function ProjectionViewer(props: {
  /** 当前选中的投影文件（空则不加载）。 */
  path?: string
  onError?: (message: string) => void
}): JSX.Element {
  const frameRef = useRef<HTMLIFrameElement | null>(null)
  const [ready, setReady] = useState(false)
  const [loading, setLoading] = useState(false)
  const [status, setStatus] = useState('等待投影数据…')
  const pathRef = useRef<string | undefined>(props.path)

  // 监听预览页发来的消息（就绪 / 提示）
  useEffect(() => {
    const onMessage = (e: MessageEvent): void => {
      const data = e.data as { type?: string } | null
      if (data === null || typeof data !== 'object') return
      if (data.type === 'ready') setReady(true)
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [])

  // 视图就绪或文件变化时推数据
  useEffect(() => {
    pathRef.current = props.path
    if (!ready || props.path === undefined || props.path === '') return
    const target = props.path
    let cancelled = false

    async function push(): Promise<void> {
      setLoading(true)
      setStatus('正在解析投影与资源…')
      try {
        const payload = await request<AssetsPayload>('lab.assets', { path: target })
        if (cancelled || pathRef.current !== target) return
        const win = frameRef.current?.contentWindow
        if (win === undefined || win === null) return

        win.postMessage(
          {
            type: 'voxelBegin',
            sizeX: payload.sizeX,
            sizeY: payload.sizeY,
            sizeZ: payload.sizeZ,
            truncated: payload.truncated,
            assetSource: payload.assetSource ?? ''
          },
          '*'
        )
        // 分块发送：单条消息过大会卡住渲染进程（Win 端同样按 200/60/20000 切）
        for (const part of chunks(Object.entries(payload.states), 200)) {
          win.postMessage({ type: 'voxelStates', items: Object.fromEntries(part) }, '*')
        }
        for (const part of chunks(Object.entries(payload.models), 200)) {
          win.postMessage({ type: 'voxelModels', items: Object.fromEntries(part) }, '*')
        }
        for (const part of chunks(Object.entries(payload.textures), 60)) {
          win.postMessage({ type: 'voxelTextures', items: Object.fromEntries(part) }, '*')
        }
        for (const part of chunks(payload.blocks, 20000)) {
          win.postMessage({ type: 'voxelBlocks', items: part }, '*')
        }
        win.postMessage({ type: 'voxelEnd' }, '*')
        setStatus('')
      } catch (e) {
        if (cancelled) return
        setStatus('')
        props.onError?.(String(e))
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void push()
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready, props.path])

  return (
    <div style={{ position: 'relative', width: '100%', height: '100%' }}>
      <iframe
        ref={frameRef}
        title="投影预览"
        src="./lab/projection-viewer.html"
        style={{ width: '100%', height: '100%', border: 'none', display: 'block' }}
      />
      {(loading || status !== '') && (
        <div
          style={{
            position: 'absolute',
            left: 12,
            top: 12,
            padding: '6px 10px',
            borderRadius: 'var(--r-sm)',
            background: 'var(--surface-acrylic)',
            backdropFilter: 'blur(12px)',
            fontSize: 12,
            color: 'var(--text-secondary)',
            pointerEvents: 'none'
          }}
        >
          {loading ? '正在解析投影与资源…' : status}
        </div>
      )}
    </div>
  )
}
