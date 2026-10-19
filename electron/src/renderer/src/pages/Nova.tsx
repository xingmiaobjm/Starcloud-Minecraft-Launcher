/// Nova AI 助手：对话页 + 模型设置。
///
/// 桥接约定很关键：`nova.chat` 不阻塞等结果，只回 `{accepted:true}`，
/// 正文靠事件流回来 —— `nova.delta` 追加文本、`nova.tool` 显示"正在调用工具"、
/// `nova.done` 收尾（带最终文本和可选的确认清单 plan[]）、`nova.failed` 报错。
/// 所以这一页的模型是"先插一条空的 AI 气泡，再靠事件往里灌"。
import { useCallback, useEffect, useRef, useState } from 'react'
import { onBridgeEvent, request } from '../bridge'
import type { NovaConfig, NovaPlanItem } from '../bridge'
import { EmptyState, Modal, PageShell, useToasts } from '../ui'

type Role = 'user' | 'ai'

interface ChatMessage {
  id: number
  role: Role
  content: string
  /** 正在流式输出的那条：气泡做呼吸动画 */
  streaming?: boolean
  /** 正在调用的工具名 */
  tool?: string
  /** 确认清单（只展示，不执行） */
  plan?: NovaPlanItem[]
  /** 清单标题，桥接侧随 plan 一起给 */
  planTitle?: string
  /** 本地能力算出来的回复，不走云端模型 */
  fromCloud?: boolean
  error?: boolean
}

export default function NovaPage() {
  const { push, host } = useToasts()

  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [config, setConfig] = useState<NovaConfig>({ baseUrl: '', apiKey: '', model: '' })
  const [configOpen, setConfigOpen] = useState(false)
  const [draft, setDraft] = useState<NovaConfig>({ baseUrl: '', apiKey: '', model: '' })

  // 自增 id：流式更新只改末尾那条，不需要按内容比对
  const seq = useRef(1)
  // 当前正在接收增量的那条 AI 消息 id
  const streamingId = useRef<number | null>(null)
  const bodyRef = useRef<HTMLDivElement | null>(null)

  // ── 模型配置 ────────────────────────────────────────────────────────

  const loadConfig = useCallback(async () => {
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

  /** 三项都空 = 清除配置（桥接侧就是这么约定的）。 */
  const saveConfig = async () => {
    try {
      const value = await request<NovaConfig>('nova.config.set', {
        baseUrl: draft.baseUrl.trim(),
        apiKey: draft.apiKey.trim(),
        model: draft.model.trim()
      })
      setConfig(value)
      setConfigOpen(false)
      const configured = value.baseUrl.length > 0 && value.model.length > 0
      push(configured ? 'success' : 'info', configured ? '模型配置已保存。' : '已清除模型配置。')
    } catch (e) {
      push('error', String(e))
    }
  }

  // ── 流式事件 ────────────────────────────────────────────────────────

  useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event === 'nova.delta') {
        const text = String(params.text ?? '')
        if (text.length === 0) return
        const id = streamingId.current
        setMessages((prev) =>
          prev.map((m) =>
            m.id === id ? { ...m, content: m.content + text, streaming: true } : m
          )
        )
      } else if (event === 'nova.tool') {
        const tool = String(params.tool ?? '')
        const id = streamingId.current
        setMessages((prev) => prev.map((m) => (m.id === id ? { ...m, tool } : m)))
      } else if (event === 'nova.done') {
        // 桥接侧 nova.done 的 params 就是平铺的 {text, fromCloud, plan}
        const text = String(params.text ?? '')
        const plan = params.plan as NovaPlanItem[] | undefined
        const planTitle =
          params.planTitle != null
            ? String(params.planTitle)
            : plan != null && plan.length > 0
              ? plan[0].title
              : undefined
        const fromCloud = Boolean(params.fromCloud)
        const id = streamingId.current
        streamingId.current = null
        setBusy(false)
        setMessages((prev) =>
          prev.map((m) =>
            m.id === id
              ? {
                  ...m,
                  // 增量已经写进去了，done 只在整段没回时兜底
                  content: text.length > 0 ? text : m.content,
                  tool: undefined,
                  streaming: false,
                  fromCloud,
                  plan,
                  planTitle
                }
              : m
          )
        )
      } else if (event === 'nova.failed') {
        const message = String(params.error ?? '未知错误')
        const id = streamingId.current
        streamingId.current = null
        setBusy(false)
        setMessages((prev) =>
          prev.map((m) =>
            m.id === id
              ? { ...m, streaming: false, tool: undefined, error: true, content: message }
              : m
          )
        )
        push('error', message)
      }
    })
  }, [push])

  // 增量很密（可能每秒几十条），合并成一次贴底
  useEffect(() => {
    const body = bodyRef.current
    if (body != null) body.scrollTop = body.scrollHeight
  }, [messages])

  // ── 发送 ────────────────────────────────────────────────────────────

  const send = async () => {
    const text = input.trim()
    if (text.length === 0 || busy) return

    const userMsg: ChatMessage = { id: seq.current++, role: 'user', content: text }
    const aiMsg: ChatMessage = {
      id: seq.current++,
      role: 'ai',
      content: '',
      streaming: true
    }
    streamingId.current = aiMsg.id
    setMessages((prev) => [...prev, userMsg, aiMsg])
    setInput('')
    setBusy(true)

    // 历史带上前面所有轮次（流式中的那条只带已收到的部分）
    const history = messages.map((m) => ({
      role: m.role === 'user' ? 'user' : 'assistant',
      content: m.content
    }))

    try {
      // 只回 accepted，正文靠上面的事件流
      await request<{ accepted: boolean }>('nova.chat', {
        message: text,
        history
      })
    } catch (e) {
      streamingId.current = null
      setBusy(false)
      setMessages((prev) =>
        prev.map((m) =>
          m.id === aiMsg.id
            ? { ...m, streaming: false, content: `发送失败：${String(e)}`, error: true }
            : m
        )
      )
      push('error', String(e))
    }
  }

  const clear = () => {
    if (busy) return
    setMessages([])
    streamingId.current = null
  }

  const configured = config.baseUrl.length > 0 && config.model.length > 0

  return (
    <>
      <PageShell
        title="Nova"
        subtitle="能读启动器状态、能调用本地工具的助手；没配模型时仍可回答本地能力范围内的问题。"
        actions={
          <div style={{ display: 'flex', gap: 8 }}>
            <button
              className="btn btn-subtle"
              onClick={() => {
                setDraft(config)
                setConfigOpen(true)
              }}
            >
              ⚙ 模型设置
            </button>
            <button className="btn btn-subtle" disabled={busy} onClick={clear}>
              ✕ 清空
            </button>
          </div>
        }
      >
        {!configured && (
          <div
            className="info info-warning"
            style={{ marginBottom: 12 }}
          >
            <span aria-hidden style={{ fontSize: 16 }}>ⓘ</span>
            <div className="info-body t-body">
              当前未配置模型：本地能力无需模型，可直接提问版本/存档/联机这类问题；
              需要联网推理时点右上角「模型设置」。
            </div>
          </div>
        )}

        <div
          className="card card-flush"
          style={{ height: 'calc(100% - 46px)', display: 'flex', flexDirection: 'column' }}
        >
          <div
            ref={bodyRef}
            style={{
              flex: 1,
              minHeight: 0,
              overflowY: 'auto',
              padding: 16,
              display: 'flex',
              flexDirection: 'column',
              gap: 12
            }}
          >
            {messages.length === 0 && (
              <EmptyState
                title="和 Nova 聊点什么"
                hint="试试：「有哪些已安装版本」「帮我把 1.20.1 的内存调到 4G」「现在联机是什么状态」。"
                action={
                  <button
                    className="btn"
                    onClick={() => setInput('现在联机是什么状态？')}
                  >
                    ✳ 来一句
                  </button>
                }
              />
            )}

            {messages.map((m, index) => (
              <div
                key={m.id}
                style={{
                  display: 'flex',
                  justifyContent: m.role === 'user' ? 'flex-end' : 'flex-start',
                  animation: `slideUp var(--t-enter) var(--e-enter) both`,
                  animationDelay: `${Math.min(index, 8) * 39}ms`
                }}
              >
                {m.role === 'user' ? (
                  // 用户气泡：品牌黄实心，右侧
                  <div className="nova-bubble-user">{m.content}</div>
                ) : (
                  // AI 气泡：暗色卡 + 左侧，形状上就分得开
                  <div
                    className="nova-bubble-ai"
                    style={{
                      borderColor: m.error === true ? 'color-mix(in srgb, var(--danger) 45%, transparent)' : undefined,
                      // 流式输出中：整块气泡轻微呼吸，告诉用户"还在写"
                      animation:
                        m.streaming === true
                          ? 'pulse 1.5s ease-in-out infinite'
                          : undefined
                    }}
                  >
                    <div className="nova-meta">
                      <span>Nova</span>
                      {m.fromCloud === true && <span>· 云端模型</span>}
                      {m.fromCloud === false && m.streaming !== true && <span>· 本地能力</span>}
                    </div>

                    {m.tool != null && m.tool.length > 0 && (
                      <div className="nova-tool">
                        ⚒ 正在调用工具：{m.tool}
                      </div>
                    )}

                    {m.content.length > 0 ? (
                      <span>{m.content}</span>
                    ) : (
                      m.streaming === true && (
                        <span>
                          <span className="nova-typing-dot" />
                          <span className="nova-typing-dot" />
                          <span className="nova-typing-dot" />
                        </span>
                      )
                    )}

                    {/* 确认清单：只展示，不代用户执行 */}
                    {m.plan != null && m.plan.length > 0 && (
                      <div className="nova-plan">
                        <div className="nova-plan-head">
                          <span style={{ color: 'var(--accent)' }}>▤</span>
                          <span className="t-body truncate" style={{ fontWeight: 600 }}>
                            {m.planTitle ?? '建议的执行清单'}
                          </span>
                          <span className="t-meta">{m.plan.length} 项</span>
                        </div>
                        {m.plan.map((item, planIndex) => (
                          <div
                            key={`${m.id}-plan-${planIndex}`}
                            style={{
                              display: 'flex',
                              alignItems: 'flex-start',
                              gap: 8,
                              padding: '7px 11px',
                              borderBottom:
                                planIndex === m.plan!.length - 1
                                  ? 'none'
                                  : '1px solid var(--stroke-divider)',
                              animation: `rowIn var(--t-enter) var(--e-enter) both`,
                              animationDelay: `${planIndex * 39}ms`
                            }}
                          >
                            <div
                              style={{
                                width: 6,
                                height: 6,
                                borderRadius: 2,
                                background: 'var(--accent)',
                                marginTop: 6,
                                flexShrink: 0
                              }}
                            />
                            <div style={{ flex: 1, minWidth: 0 }}>
                              <div className="t-body" style={{ fontWeight: 600 }}>
                                {item.title}
                              </div>
                              {item.detail != null && item.detail.length > 0 && (
                                <div className="t-meta">{item.detail}</div>
                              )}
                            </div>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                )}
              </div>
            ))}
          </div>

          <div className="nova-composer">
            <textarea
              className="input nova-textarea"
              value={input}
              placeholder="问点什么…（Enter 发送，Shift+Enter 换行）"
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  void send()
                }
              }}
            />
            <button
              className="btn btn-primary"
              style={{ height: 38, padding: '0 18px' }}
              disabled={input.trim().length === 0 || busy}
              onClick={() => void send()}
            >
              {busy ? <span className="spinner" /> : '➤'}
              {busy ? '思考中…' : '发送'}
            </button>
          </div>
        </div>
        {host}
      </PageShell>

      {/* ── 模型设置 ── */}
      <Modal
        open={configOpen}
        title="模型设置"
        width={560}
        onClose={() => setConfigOpen(false)}
        footer={
          <>
            <button className="btn" onClick={() => setConfigOpen(false)}>
              取消
            </button>
            <button className="btn btn-primary" onClick={() => void saveConfig()}>
              {!configured && '⚠ '}保存
            </button>
          </>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div className="t-body" style={{ opacity: 0.7 }}>
            三项都留空即可清除配置，回到纯本地能力模式。API Key 只保存在本机设置里。
          </div>
          <div>
            <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
              Base URL
            </div>
            <input
              className="input"
              style={{ width: '100%' }}
              value={draft.baseUrl}
              placeholder="https://api.example.com/v1"
              onChange={(e) => setDraft({ ...draft, baseUrl: e.target.value })}
            />
          </div>
          <div>
            <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
              API Key
            </div>
            <input
              className="input"
              style={{ width: '100%' }}
              type="password"
              value={draft.apiKey}
              placeholder="sk-..."
              onChange={(e) => setDraft({ ...draft, apiKey: e.target.value })}
            />
          </div>
          <div>
            <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
              模型
            </div>
            <input
              className="input"
              style={{ width: '100%' }}
              value={draft.model}
              placeholder="例如 gpt-4o-mini"
              onChange={(e) => setDraft({ ...draft, model: e.target.value })}
            />
          </div>
        </div>
      </Modal>
      {/* 气泡 / 输入条的形状写在 winui.css 之外（对话布局专用），就地声明一次 */}
      <style>{`
.nova-bubble-user{max-width:68%;padding:10px 14px;border-radius:var(--r-card);border-bottom-right-radius:4px;background:var(--accent);color:var(--text-on-accent);font-size:13px;line-height:1.6;white-space:pre-wrap;word-break:break-word;box-shadow:0 2px 10px rgba(255,194,31,0.18)}
.nova-bubble-ai{max-width:76%;padding:12px 15px;border-radius:var(--r-card);border-bottom-left-radius:4px;background:var(--surface-card);border:1px solid var(--stroke-subtle);font-size:13px;line-height:1.7;white-space:pre-wrap;word-break:break-word}
.nova-meta{display:flex;align-items:center;gap:6px;margin-bottom:5px;font-size:10.5px;opacity:0.55}
.nova-tool{display:inline-flex;align-items:center;gap:6px;padding:3px 9px;border-radius:var(--r-pill);background:var(--surface-subtle);border:1px solid var(--stroke-default);font-size:11px;opacity:0.85;margin-bottom:6px;animation:fadeIn var(--t-enter) var(--e-enter) both}
.nova-plan{margin-top:10px;border-radius:var(--r-card);border:1.2px solid color-mix(in srgb, var(--accent) 35%, transparent);background:color-mix(in srgb, var(--accent) 6%, transparent);overflow:hidden}
.nova-plan-head{display:flex;align-items:center;gap:6px;padding:8px 11px;border-bottom:1px solid var(--stroke-divider);background:color-mix(in srgb, var(--accent) 8%, transparent)}
.nova-composer{display:flex;align-items:flex-end;gap:10px;padding:12px 14px;border-top:1px solid var(--stroke-divider)}
.nova-textarea{flex:1;height:auto;min-height:38px;max-height:180px;padding:8px 10px;resize:none;line-height:1.5;font-family:inherit}
.nova-typing-dot{display:inline-block;width:6px;height:6px;border-radius:50%;background:var(--accent);margin-right:4px;animation:pulse 1.2s infinite}
`}</style>
    </>
  )
}
