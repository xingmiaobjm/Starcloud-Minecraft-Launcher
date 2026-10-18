/// Nova AI 助手：对话页 + 模型设置。
///
/// 桥接约定很关键：`nova.chat` 不阻塞等结果，只回 `{accepted:true}`，
/// 正文靠事件流回来 —— `nova.delta` 追加文本、`nova.tool` 显示"正在调用工具"、
/// `nova.done` 收尾（带最终文本和可选的确认清单 plan[]）、`nova.failed` 报错。
/// 所以这一页的模型是"先插一条空的 AI 气泡，再靠事件往里灌"。
import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  makeStyles
} from '@fluentui/react-components'
import {
  ChatRegular,
  DismissRegular,
  ErrorCircleRegular,
  SendRegular,
  SettingsRegular,
  WrenchRegular
} from '@fluentui/react-icons'
import { onBridgeEvent, request } from '../bridge'
import type { NovaConfig, NovaPlanItem } from '../bridge'
import { brandYellow, brandYellowHover, motion } from '../theme'
import { EmptyState, PageShell, useToasts } from '../ui'

const useStyles = makeStyles({
  // 对话流占满剩余高度，输入条永远贴在底部
  chatCard: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    minHeight: 0,
    padding: '0'
  },
  chatBody: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px'
  },
  msgRow: {
    display: 'flex',
    animation: `slideUp ${motion.normal}ms ${motion.easeOut} both`
  },
  msgRowUser: { justifyContent: 'flex-end' },
  msgRowAi: { justifyContent: 'flex-start' },
  // 用户气泡：品牌黄实心，右侧
  bubbleUser: {
    maxWidth: '68%',
    padding: '10px 14px',
    borderRadius: '12px',
    borderBottomRightRadius: '4px',
    background: brandYellow,
    color: '#1b1b1b',
    fontSize: '13px',
    lineHeight: '1.6',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    boxShadow: `0 2px 10px rgba(255,194,31,0.18)`
  },
  // AI 气泡：暗色卡 + 左侧，形状上就分得开
  bubbleAi: {
    maxWidth: '76%',
    padding: '12px 15px',
    borderRadius: '12px',
    borderBottomLeftRadius: '4px',
    background: 'rgba(255,255,255,0.05)',
    border: '1px solid rgba(255,255,255,0.08)',
    fontSize: '13px',
    lineHeight: '1.7',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word'
  },
  // 流式输出中：整块气泡轻微呼吸，告诉用户"还在写"
  streaming: { animation: 'pulse 1.5s ease-in-out infinite' },
  meta: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    marginBottom: '5px',
    fontSize: '10.5px',
    opacity: 0.55
  },
  toolRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '3px 9px',
    borderRadius: '999px',
    background: 'rgba(255,255,255,0.06)',
    border: '1px solid rgba(255,255,255,0.09)',
    fontSize: '11px',
    opacity: 0.85,
    marginBottom: '6px',
    animation: `fadeIn ${motion.normal}ms ${motion.ease} both`
  },
  // 确认清单卡片：只展示，不执行
  planCard: {
    marginTop: '10px',
    borderRadius: '8px',
    border: `1.2px solid rgba(255,194,31,0.35)`,
    background: 'rgba(255,194,31,0.06)',
    overflow: 'hidden'
  },
  planHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 11px',
    borderBottom: '1px solid rgba(255,255,255,0.08)',
    background: 'rgba(255,194,31,0.08)'
  },
  planItem: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '8px',
    padding: '7px 11px',
    borderBottom: '1px solid rgba(255,255,255,0.05)',
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`
  },
  planItemLast: { borderBottom: 'none' },
  planBullet: {
    width: '6px',
    height: '6px',
    borderRadius: '2px',
    background: brandYellow,
    marginTop: '6px',
    flexShrink: 0
  },
  composer: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '10px',
    padding: '12px 14px',
    borderTop: '1px solid rgba(255,255,255,0.07)'
  },
  primaryButton: {
    background: brandYellow,
    color: '#1b1b1b',
    fontWeight: 700,
    border: 'none',
    transition: `background ${motion.fast}ms ${motion.ease}, transform ${motion.fast}ms ${motion.ease}, box-shadow ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: brandYellowHover, color: '#1b1b1b' },
    ':active': { transform: 'scale(0.985)' }
  },
  primaryGlow: { boxShadow: `0 0 0 0 rgba(255,194,31,0.45)` },
  typingDot: {
    display: 'inline-block',
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    background: brandYellow,
    marginRight: '4px',
    animation: 'pulse 1.2s infinite'
  },
  configRow: { display: 'flex', flexDirection: 'column', gap: '12px' }
})

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
  const styles = useStyles()
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
          <div style={{ display: 'flex', gap: '8px' }}>
            <Button
              appearance="subtle"
              icon={<SettingsRegular />}
              onClick={() => {
                setDraft(config)
                setConfigOpen(true)
              }}
            >
              模型设置
            </Button>
            <Button appearance="subtle" icon={<DismissRegular />} disabled={busy} onClick={clear}>
              清空
            </Button>
          </div>
        }
      >
        {!configured && (
          <MessageBar intent="info" style={{ marginBottom: '12px' }}>
            <MessageBarBody>
              当前未配置模型：本地能力无需模型，可直接提问版本/存档/联机这类问题；
              需要联网推理时点右上角「模型设置」。
            </MessageBarBody>
          </MessageBar>
        )}

        <Card className={styles.chatCard} style={{ height: 'calc(100% - 46px)' }}>
          <div className={styles.chatBody} ref={bodyRef}>
            {messages.length === 0 && (
              <EmptyState
                title="和 Nova 聊点什么"
                hint="试试：「有哪些已安装版本」「帮我把 1.20.1 的内存调到 4G」「现在联机是什么状态」。"
                action={
                  <Button
                    icon={<ChatRegular />}
                    onClick={() => setInput('现在联机是什么状态？')}
                  >
                    来一句
                  </Button>
                }
              />
            )}

            {messages.map((m, index) => (
              <div
                key={m.id}
                className={[styles.msgRow, m.role === 'user' ? styles.msgRowUser : styles.msgRowAi]
                  .join(' ')}
                style={{ animationDelay: `${Math.min(index, 8) * 24}ms` }}
              >
                {m.role === 'user' ? (
                  <div className={styles.bubbleUser}>{m.content}</div>
                ) : (
                  <div
                    className={[
                      styles.bubbleAi,
                      m.streaming === true ? styles.streaming : ''
                    ]
                      .filter(Boolean)
                      .join(' ')}
                    style={{ borderColor: m.error === true ? 'rgba(224,75,75,0.45)' : undefined }}
                  >
                    <div className={styles.meta}>
                      <span>Nova</span>
                      {m.fromCloud === true && <span>· 云端模型</span>}
                      {m.fromCloud === false && m.streaming !== true && <span>· 本地能力</span>}
                    </div>

                    {m.tool != null && m.tool.length > 0 && (
                      <div className={styles.toolRow}>
                        <WrenchRegular />
                        正在调用工具：{m.tool}
                      </div>
                    )}

                    {m.content.length > 0 ? (
                      <span>{m.content}</span>
                    ) : (
                      m.streaming === true && (
                        <span>
                          <span className={styles.typingDot} />
                          <span className={styles.typingDot} />
                          <span className={styles.typingDot} />
                        </span>
                      )
                    )}

                    {/* 确认清单：只展示，不代用户执行 */}
                    {m.plan != null && m.plan.length > 0 && (
                      <div className={styles.planCard}>
                        <div className={styles.planHeader}>
                          <ClipboardGlyph />
                          <Text size={200} weight="semibold" truncate>
                            {m.planTitle ?? '建议的执行清单'}
                          </Text>
                          <Text size={100} style={{ opacity: 0.6 }}>
                            {m.plan.length} 项
                          </Text>
                        </div>
                        {m.plan.map((item, planIndex) => (
                          <div
                            key={`${m.id}-plan-${planIndex}`}
                            className={[
                              styles.planItem,
                              planIndex === m.plan!.length - 1 ? styles.planItemLast : ''
                            ]
                              .filter(Boolean)
                              .join(' ')}
                            style={{ animationDelay: `${planIndex * 24}ms` }}
                          >
                            <div className={styles.planBullet} />
                            <div style={{ flex: 1, minWidth: 0 }}>
                              <Text size={200} weight="semibold" style={{ display: 'block' }}>
                                {item.title}
                              </Text>
                              {item.detail != null && item.detail.length > 0 && (
                                <Text size={100} style={{ opacity: 0.65, display: 'block' }}>
                                  {item.detail}
                                </Text>
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

          <div className={styles.composer}>
            <Textarea
              style={{ flex: 1, resize: 'none' }}
              value={input}
              placeholder="问点什么…（Enter 发送，Shift+Enter 换行）"
              onChange={(_, data) => setInput(data.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  void send()
                }
              }}
            />
            <button
              className={[
                styles.primaryButton,
                input.trim().length > 0 && !busy ? styles.primaryGlow : ''
              ]
                .filter(Boolean)
                .join(' ')}
              style={{
                height: '38px',
                padding: '0 18px',
                borderRadius: '8px',
                display: 'flex',
                alignItems: 'center',
                gap: '6px',
                cursor: input.trim().length > 0 && !busy ? 'pointer' : 'default',
                opacity: input.trim().length > 0 && !busy ? 1 : 0.45
              }}
              disabled={input.trim().length === 0 || busy}
              onClick={() => void send()}
            >
              {busy ? <Spinner size="tiny" /> : <SendRegular />}
              {busy ? '思考中…' : '发送'}
            </button>
          </div>
        </Card>
      </PageShell>

      {/* ── 模型设置 ── */}
      <Dialog
        open={configOpen}
        onOpenChange={(_, data) => setConfigOpen(data.open)}
        modalType="modal"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>模型设置</DialogTitle>
            <DialogContent>
              <div className={styles.configRow}>
                <Text size={200} style={{ opacity: 0.7 }}>
                  三项都留空即可清除配置，回到纯本地能力模式。API Key 只保存在本机设置里。
                </Text>
                <Field label="Base URL" size="small">
                  <Input
                    value={draft.baseUrl}
                    placeholder="https://api.example.com/v1"
                    onChange={(_, data) => setDraft({ ...draft, baseUrl: data.value })}
                  />
                </Field>
                <Field label="API Key" size="small">
                  <Input
                    type="password"
                    value={draft.apiKey}
                    placeholder="sk-..."
                    onChange={(_, data) => setDraft({ ...draft, apiKey: data.value })}
                  />
                </Field>
                <Field label="模型" size="small">
                  <Input
                    value={draft.model}
                    placeholder="例如 gpt-4o-mini"
                    onChange={(_, data) => setDraft({ ...draft, model: data.value })}
                  />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setConfigOpen(false)}>
                取消
              </Button>
              <Button
                appearance="primary"
                className={styles.primaryButton}
                icon={configured ? undefined : <ErrorCircleRegular />}
                onClick={() => void saveConfig()}
              >
                保存
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {host}
    </>
  )
}

/** 清单标题左侧的小图标（inline SVG，避免为一个图标再引一个包）。 */
function ClipboardGlyph() {
  return (
    <svg width="13" height="13" viewBox="0 0 16 16" fill="none" aria-hidden>
      <path
        d="M6 2.5h4v1.5H6zM4 3.5H3v10h10v-10h-1"
        stroke={brandYellow}
        strokeWidth="1.2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <path
        d="M5.5 7.5h5M5.5 10h5"
        stroke={brandYellow}
        strokeWidth="1.2"
        strokeLinecap="round"
      />
    </svg>
  )
}
