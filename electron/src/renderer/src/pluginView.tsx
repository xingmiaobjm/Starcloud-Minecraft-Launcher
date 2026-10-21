/// 插件 2.0 声明式视图渲染器（对齐 Win 端 PluginViewRenderer）。
/// 白名单组件：stack / text / button / toggle / input / select / list / table /
/// image / progress / badge / divider / link / code。
/// 动作 `{api, args}` 与 `{handler}` 都走 `plugins.invoke`。
import { useEffect, useState } from 'react'

import { request } from './bridge'
import { IconClose } from './icons'

export interface PluginViewBinding {
  scope: string
  key: string
}

export interface PluginViewAction {
  api: string
  argsJson: string
  handler: string
}

export interface PluginViewNode {
  type: string
  value?: { text: string; bind?: PluginViewBinding | null } | null
  itemsBind?: PluginViewBinding | null
  bind?: PluginViewBinding | null
  itemView?: string
  style?: string
  direction?: string
  kind?: string
  placeholder?: string
  source?: string
  href?: string
  language?: string
  wrap?: boolean
  multiline?: boolean
  spacing?: number
  height?: number
  progress?: number
  options?: string[]
  columns?: string[]
  action?: PluginViewAction | null
  children?: PluginViewNode[]
}

export interface PluginViewResult {
  ok: boolean
  errors?: string[]
  view?: PluginViewNode | null
  bindings?: Record<string, unknown>
}

function textOf(node: PluginViewNode, bindings: Record<string, unknown>): string {
  if (node.value === undefined || node.value === null) return ''
  if (node.value.bind !== undefined && node.value.bind !== null) {
    const key = `${node.value.bind.scope}.${node.value.bind.key}`
    const v = bindings[key]
    return v === undefined || v === null ? '' : String(v)
  }
  return node.value.text
}

/** 单节点渲染。ctx 提供插件 id 与刷新回调（写操作后重新拉取 bindings）。 */
export function PluginViewNodeView(props: {
  node: PluginViewNode
  bindings: Record<string, unknown>
  pluginId: string
  onRefresh: () => void
  onError: (message: string) => void
}): JSX.Element {
  const { node, bindings, pluginId } = props
  const [busy, setBusy] = useState(false)

  async function run(action: PluginViewAction | null | undefined): Promise<void> {
    if (action === undefined || action === null) return
    const api = action.handler.length > 0 ? action.handler : action.api
    if (api === '') return
    let args: unknown = []
    try {
      args = JSON.parse(action.argsJson === '' ? '[]' : action.argsJson)
    } catch {
      args = []
    }
    setBusy(true)
    try {
      const r = await request<{ ok: boolean; result?: string; error?: string }>('plugins.invoke', {
        id: pluginId,
        api,
        args
      })
      if (!r.ok) props.onError(r.error ?? '动作执行失败')
      props.onRefresh()
    } catch (e) {
      props.onError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const children = (node.children ?? []).map((child, i) => (
    <PluginViewNodeView
      key={`${child.type}-${i}`}
      node={child}
      bindings={bindings}
      pluginId={pluginId}
      onRefresh={props.onRefresh}
      onError={props.onError}
    />
  ))

  switch (node.type) {
    case 'stack':
      return (
        <div
          style={{
            display: 'flex',
            flexDirection: node.direction === 'row' ? 'row' : 'column',
            gap: node.spacing !== undefined && node.spacing > 0 ? node.spacing : 8,
            alignItems: node.direction === 'row' ? 'center' : 'stretch',
            flexWrap: 'wrap'
          }}
        >
          {children}
        </div>
      )

    case 'text':
      return (
        <div
          className="t-body"
          style={{ whiteSpace: node.wrap === false ? 'nowrap' : 'pre-wrap' }}
        >
          {textOf(node, bindings)}
        </div>
      )

    case 'title':
      return <div className="t-strong">{textOf(node, bindings)}</div>

    case 'caption':
      return <div className="t-cap">{textOf(node, bindings)}</div>

    case 'button':
      return (
        <button
          className={`btn${node.kind === 'accent' ? ' btn-primary' : ''}${node.kind === 'danger' ? ' btn-danger' : ''}`}
          disabled={busy}
          onClick={() => void run(node.action)}
        >
          {textOf(node, bindings)}
        </button>
      )

    case 'toggle': {
      const key =
        node.bind !== undefined && node.bind !== null ? `${node.bind.scope}.${node.bind.key}` : ''
      const checked = bindings[key] === true || bindings[key] === 'true'
      return (
        <label style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <button
            className="toggle"
            role="switch"
            aria-checked={checked}
            disabled={busy}
            onClick={() => void run(node.action)}
          />
          <span className="t-body">{textOf(node, bindings)}</span>
        </label>
      )
    }

    case 'input': {
      const key =
        node.bind !== undefined && node.bind !== null ? `${node.bind.scope}.${node.bind.key}` : ''
      const value = bindings[key]
      return (
        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span className="t-cap">{textOf(node, bindings)}</span>
          {node.multiline === true ? (
            <textarea
              className="input"
              style={{ minHeight: 72 }}
              placeholder={node.placeholder}
              defaultValue={value === undefined || value === null ? '' : String(value)}
            />
          ) : (
            <input
              className="input"
              placeholder={node.placeholder}
              defaultValue={value === undefined || value === null ? '' : String(value)}
            />
          )}
        </label>
      )
    }

    case 'select': {
      const key =
        node.bind !== undefined && node.bind !== null ? `${node.bind.scope}.${node.bind.key}` : ''
      const value = bindings[key]
      return (
        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span className="t-cap">{textOf(node, bindings)}</span>
          <select className="input" defaultValue={value === undefined || value === null ? '' : String(value)}>
            {(node.options ?? []).map((opt) => (
              <option key={opt} value={opt}>
                {opt}
              </option>
            ))}
          </select>
        </label>
      )
    }

    case 'list': {
      const key =
        node.itemsBind !== undefined && node.itemsBind !== null
          ? `${node.itemsBind.scope}.${node.itemsBind.key}`
          : ''
      const raw = bindings[key]
      const items = Array.isArray(raw) ? raw : []
      if (items.length === 0) {
        return <div className="t-cap">（空）</div>
      }
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {items.map((item, i) => (
            <div key={i} className="row row-static" style={{ minHeight: 0 }}>
              <span className="t-body">
                {typeof item === 'string' ? item : JSON.stringify(item)}
              </span>
            </div>
          ))}
        </div>
      )
    }

    case 'table': {
      const key =
        node.itemsBind !== undefined && node.itemsBind !== null
          ? `${node.itemsBind.scope}.${node.itemsBind.key}`
          : ''
      const raw = bindings[key]
      const rows = Array.isArray(raw) ? raw : []
      const cols = node.columns ?? []
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <div style={{ display: 'flex', gap: 12 }}>
            {cols.map((c) => (
              <span key={c} className="t-cap" style={{ flex: 1 }}>
                {c}
              </span>
            ))}
          </div>
          {rows.map((row, i) => (
            <div key={i} style={{ display: 'flex', gap: 12 }}>
              {cols.map((c) => (
                <span key={c} className="t-body" style={{ flex: 1 }}>
                  {typeof row === 'object' && row !== null
                    ? String((row as Record<string, unknown>)[c] ?? '')
                    : ''}
                </span>
              ))}
            </div>
          ))}
        </div>
      )
    }

    case 'image':
      return node.source === '' ? (
        <div className="t-cap">（图片）</div>
      ) : (
        <img
          src={node.source}
          alt=""
          style={{ maxWidth: '100%', borderRadius: 'var(--r-sm)' }}
        />
      )

    case 'progress':
      return (
        <div className="progress" style={{ width: '100%' }}>
          <div
            className="progress-fill"
            style={{ width: `${Math.max(0, Math.min(100, node.progress ?? 0))}%` }}
          />
        </div>
      )

    case 'badge':
      return <span className="chip chip-accent">{textOf(node, bindings)}</span>

    case 'divider':
      return <div style={{ height: 1, background: 'var(--stroke-divider)' }} />

    case 'link':
      return (
        <a
          className="t-body"
          style={{ color: 'var(--accent)' }}
          href={node.href}
          onClick={(e) => {
            e.preventDefault()
            void run(node.action)
          }}
        >
          {textOf(node, bindings)}
        </a>
      )

    case 'code':
      return (
        <pre
          className="log"
          style={{
            margin: 0,
            padding: 10,
            background: 'var(--surface-subtle)',
            borderRadius: 'var(--r-sm)',
            maxHeight: (node.height ?? 0) > 0 ? node.height : 240
          }}
        >
          {textOf(node, bindings)}
        </pre>
      )

    default:
      return <div className="t-cap">未支持的组件：{node.type}</div>
  }
}

/** 面板视图容器：加载 plugins.view 并按声明式树渲染。 */
export function PluginPanelView(props: {
  pluginId: string
  panelId: string
  onError: (message: string) => void
}): JSX.Element {
  const [result, setResult] = useState<PluginViewResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [tick, setTick] = useState(0)

  useEffect(() => {
    let alive = true
    setLoading(true)
    void request<PluginViewResult>('plugins.view', {
      id: props.pluginId,
      panelId: props.panelId
    })
      .then((r) => {
        if (alive) setResult(r)
      })
      .catch((e) => {
        if (alive) props.onError(String(e))
      })
      .finally(() => {
        if (alive) setLoading(false)
      })
    return () => {
      alive = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.pluginId, props.panelId, tick])

  if (loading) return <div className="t-cap">加载面板…</div>
  if (result === null) return <div className="t-cap">面板加载失败</div>

  if (!result.ok) {
    return (
      <div className="info info-danger">
        <IconClose size={14} />
        <div className="info-body">
          <div className="t-strong">面板定义不合法</div>
          {(result.errors ?? []).map((e) => (
            <div key={e} className="t-cap">
              {e}
            </div>
          ))}
        </div>
      </div>
    )
  }

  const view = result.view ?? null
  if (view === null) return <div className="t-cap">面板没有内容</div>

  return (
    <PluginViewNodeView
      node={view}
      bindings={result.bindings ?? {}}
      pluginId={props.pluginId}
      onRefresh={() => setTick((n) => n + 1)}
      onError={props.onError}
    />
  )
}
