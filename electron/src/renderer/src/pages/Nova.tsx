/// Nova 页：对齐 Win 端 NovaPage —— 这是**模型设置页**，不是聊天页。
/// 卡片顺序：Nova 账户 → 云端模型 → 模型设置 → 游戏内辅助浮窗。
/// 对话按 Win 端的交互放在独立浮层里，由「打开 Nova 对话」按钮唤起。
import { useCallback, useEffect, useRef, useState } from 'react'

import { onBridgeEvent, request } from '../bridge'
import type { NovaConfig, NovaPlanItem } from '../bridge'
import { Modal, Toggle, useToasts } from '../ui'

/** 分组卡标题（黄色竖条 + 标题 + 右侧附加操作）。 */
function SectionCard(props: {
  title: string
  action?: React.ReactNode
  children: React.ReactNode
}): JSX.Element {
  return (
    <div className="card">
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 10 }}>
        <span
          style={{ width: 4, height: 16, borderRadius: 2, background: 'var(--accent)', flexShrink: 0 }}
        />
        <span className="t-strong" style={{ fontSize: 15 }}>
          {props.title}
        </span>
        <div style={{ flex: 1 }} />
        {props.action}
      </div>
      {props.children}
    </div>
  )
}

interface ChatMessage {
  id: number
  role: 'user' | 'assistant'
  content: string
  tool?: string
  streaming?: boolean
  plan?: NovaPlanItem[]
}

export default function NovaPage(): JSX.Element {
  const { push, host } = useToasts()
  const [config, setConfig] = useState<NovaConfig>({ baseUrl: '', apiKey: '', model: '' })
  const [draft, setDraft] = useState<NovaConfig>({ baseUrl: '', apiKey: '', model: '' })
  const [useCloud, setUseCloud] = useState(false)
  const [overlay, setOverlay] = useState(false)
  const [chatOpen, setChatOpen] = useState(false)

  // 对话状态
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const seq = useRef(1)
  const streamingId = useRef<number | null>(null)
  const bodyRef = useRef<HTMLDivElement | null>(null)

  const loadConfig = useCallback(async (): Promise<void> => {
    try {
      const value = await request<NovaConfig>('nova.config.get')
      setConfig(value)
      setDraft(value)
    } catch (e) {
      push('error', `读取模型配置失败：${String(e)}`)
    }
  }, [push])

  useEffect(() => {
    void loadConfig()
  }, [loadConfig])

  const configured = config.baseUrl.length > 0 && config.model.length > 0

  async function saveConfig(): Promise<void> {
    try {
      const value = await request<NovaConfig>('nova.config.set', {
        baseUrl: draft.baseUrl.trim(),
        apiKey: draft.apiKey.trim(),
        model: draft.model.trim()
      })
      setConfig(value)
      const ok = value.baseUrl.length > 0 && value.model.length > 0
      push(ok ? 'success' : 'info', ok ? '模型配置已保存。' : '已清除模型配置。')
    } catch (e) {
      push('error', String(e))
    }
  }

  // 流式事件（nova.chat 只回 accepted，正文走事件）
  useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event === 'nova.delta') {
        const text = String(params.text ?? '')
        if (text === '') return
        const id = streamingId.current
        setMessages((prev) =>
          prev.map((m) => (m.id === id ? { ...m, content: m.content + text, streaming: true } : m))
        )
      } else if (event === 'nova.tool') {
        const tool = String(params.tool ?? '')
        const id = streamingId.current
        setMessages((prev) => prev.map((m) => (m.id === id ? { ...m, tool } : m)))
      } else if (event === 'nova.done') {
        const text = String(params.text ?? '')
        const plan = params.plan as NovaPlanItem[] | undefined
        const id = streamingId.current
        streamingId.current = null
        setBusy(false)
        setMessages((prev) =>
          prev.map((m) =>
            m.id === id
              ? { ...m, content: text !== '' ? text : m.content, streaming: false, plan }
              : m
          )
        )
      } else if (event === 'nova.failed') {
        const error = String(params.error ?? '未知错误')
        const id = streamingId.current
        streamingId.current = null
        setBusy(false)
        setMessages((prev) =>
          prev.map((m) =>
            m.id === id
              ? { ...m, content: `请求失败：${error}`, streaming: false }
              : m
          )
        )
      }
    })
  }, [])

  useEffect(() => {
    if (bodyRef.current !== null) {
      bodyRef.current.scrollTop = bodyRef.current.scrollHeight
    }
  }, [messages])

  async function send(): Promise<void> {
    const text = input.trim()
    if (text === '' || busy) return
    if (!configured) {
      push('info', '先在「模型设置」里填好 Base URL 与模型名')
      return
    }
    setInput('')
    setBusy(true)
    const userId = seq.current++
    const assistantId = seq.current++
    streamingId.current = assistantId
    setMessages((prev) => [
      ...prev,
      { id: userId, role: 'user', content: text },
      { id: assistantId, role: 'assistant', content: '', streaming: true }
    ])
    try {
      await request<{ accepted: boolean }>('nova.chat', { message: text })
    } catch (e) {
      streamingId.current = null
      setBusy(false)
      setMessages((prev) =>
        prev.map((m) =>
          m.id === assistantId ? { ...m, content: `请求失败：${String(e)}`, streaming: false } : m
        )
      )
    }
  }

  return (
    <div className="page">
      <div className="page-body" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {/* Nova 账户 */}
        <SectionCard
          title="Nova 账户"
          action={
            <button
              className="btn btn-sm"
              onClick={() => push('info', 'Starcloud ID 登录：内核能力接入后可用')}
            >
              登录
            </button>
          }
        >
          <div className="t-cap" style={{ marginBottom: 10 }}>
            登录后可用云端模型与邀请、时刻等在线能力；凭据只保存在本机。
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <span style={{ fontSize: 18, color: 'var(--accent)' }}>✦</span>
            <div style={{ flex: 1 }}>
              <div className="t-body">未登录</div>
              <div className="t-meta">当前使用下面接入的本地模型</div>
            </div>
          </div>
        </SectionCard>

        {/* 云端模型 */}
        <SectionCard
          title="云端模型"
          action={
            <button className="btn btn-sm" onClick={() => void loadConfig()}>
              刷新
            </button>
          }
        >
          <div className="t-cap" style={{ marginBottom: 10 }}>
            登录 Starcloud ID 后 Nova 默认走云端模型，不用自己填 Key；用量记在账号上。
          </div>
          <div className="t-body" style={{ marginBottom: 6 }}>
            使用云端模型
          </div>
          <Toggle
            checked={useCloud}
            disabled={true}
            label="使用云端模型"
            onChange={setUseCloud}
          />
          <div className="t-cap" style={{ marginTop: 10 }}>
            未登录时不可用；关掉后 Nova 只用下面接入的模型。
          </div>
        </SectionCard>

        {/* 模型设置 */}
        <SectionCard
          title="模型设置"
          action={
            <button className="btn btn-sm" onClick={() => void saveConfig()}>
              保存
            </button>
          }
        >
          <div className="t-cap" style={{ marginBottom: 12 }}>
            接入任意 OpenAI 兼容服务：填 Base URL、Key 与模型名。Key 只保存在本机。
          </div>

          {configured && (
            <div
              className="row row-static"
              style={{ background: 'var(--surface-subtle)', marginBottom: 12 }}
            >
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span className="t-strong truncate">{config.model}</span>
                  <span className="chip chip-accent">当前使用</span>
                </div>
                <div className="t-meta truncate">
                  {config.baseUrl} · Key {config.apiKey === '' ? '未设置' : config.apiKey.slice(0, 8) + '…'}
                </div>
              </div>
            </div>
          )}

          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <span className="t-cap">Base URL</span>
              <input
                className="input"
                value={draft.baseUrl}
                placeholder="例如 https://api.example.com/v1"
                onChange={(e) => setDraft({ ...draft, baseUrl: e.target.value })}
              />
            </label>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <span className="t-cap">API Key</span>
              <input
                className="input"
                type="password"
                value={draft.apiKey}
                placeholder="sk-…"
                onChange={(e) => setDraft({ ...draft, apiKey: e.target.value })}
              />
            </label>
            <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <span className="t-cap">模型名</span>
              <input
                className="input"
                value={draft.model}
                placeholder="例如 gpt-4o-mini / deepseek-chat"
                onChange={(e) => setDraft({ ...draft, model: e.target.value })}
              />
            </label>
          </div>

          <div style={{ marginTop: 14 }}>
            <button className="btn btn-primary" onClick={() => setChatOpen(true)}>
              打开 Nova 对话
            </button>
          </div>
        </SectionCard>

        {/* 游戏内辅助浮窗 */}
        <SectionCard title="游戏内辅助浮窗">
          <div className="t-cap" style={{ marginBottom: 10 }}>
            启动游戏时自动弹出浮窗，游戏内随时查资料；与顶栏 Nova 共用同一份对话。
          </div>
          <Toggle
            checked={overlay}
            onChange={(v) => {
              setOverlay(v)
              if (v) push('info', '游戏内浮窗：内核能力接入后可用')
            }}
            label="游戏内辅助浮窗"
          />
        </SectionCard>
      </div>

      {/* Nova 对话浮层 */}
      <Modal open={chatOpen} title="Nova" onClose={() => setChatOpen(false)} width={720}>
        <div
          ref={bodyRef}
          style={{ maxHeight: '52vh', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 10 }}
        >
          {messages.length === 0 && (
            <div className="t-cap" style={{ padding: '16px 0' }}>
              {configured
                ? '问点什么吧：查版本、算配置、写指令都可以。'
                : '先在「模型设置」里填好 Base URL 与模型名。'}
            </div>
          )}
          {messages.map((m) => (
            <div
              key={m.id}
              style={{
                alignSelf: m.role === 'user' ? 'flex-end' : 'flex-start',
                maxWidth: '78%',
                padding: '8px 12px',
                borderRadius: 12,
                background:
                  m.role === 'user' ? 'var(--accent)' : 'var(--surface-subtle)',
                color: m.role === 'user' ? 'var(--text-on-accent)' : 'var(--text-primary)',
                border: m.role === 'user' ? 'none' : '1px solid var(--stroke-subtle)',
                whiteSpace: 'pre-wrap',
                fontSize: 13,
                animation: m.streaming ? 'pulse 1.5s infinite' : undefined
              }}
            >
              {m.tool !== undefined && (
                <div className="t-meta" style={{ marginBottom: 4, opacity: 0.8 }}>
                  正在调用：{m.tool}
                </div>
              )}
              {m.content}
              {m.plan !== undefined && m.plan.length > 0 && (
                <div style={{ marginTop: 8, display: 'flex', flexDirection: 'column', gap: 4 }}>
                  {m.plan.map((p) => (
                    <div
                      key={p.title}
                      className="t-meta"
                      style={{ padding: '6px 8px', borderRadius: 8, background: 'var(--surface-hover)' }}
                    >
                      <b>{p.title}</b>
                      {p.detail !== '' ? ` — ${p.detail}` : ''}
                    </div>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>

        <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
          <input
            className="input"
            style={{ flex: 1 }}
            value={input}
            placeholder="给 Nova 发消息…"
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) void send()
            }}
          />
          <button className="btn btn-primary" disabled={busy || input.trim() === ''} onClick={() => void send()}>
            {busy ? '回复中…' : '发送'}
          </button>
        </div>
      </Modal>

      {host}
    </div>
  )
}
