/// 实验室（投影工坊 + 世吞生成器）：解析 .litematic 投影并画等距体素预览。
///
/// 3D 预览刻意不用 WebGL、也不引新依赖：一块 <canvas> + 每帧手绘等距立方体。
/// 画法是经典的两面可见投影 —— 每个方块画顶面（菱形）和右侧面（平行四边形），
/// 按 (x+y+z) 深度排序后由远及近绘制，后画的自然盖住先画的，颜色由 block id 哈希成 HSL。
/// 视图变换（缩放/平移）存在 useRef 里，滚轮与拖拽只改 ref 并触发重绘，不进 React 状态。
import { useCallback, useEffect, useRef, useState } from 'react'
import { request } from '../bridge'
import type { ProjectionPreview } from '../bridge'
import { EmptyState, PageShell, SkeletonList, useToasts } from '../ui'

/** 统计列表最多展示这么多条（桥接侧本身也只给 60 条）。 */
const MAX_STATS_ROWS = 30

/** block id → 稳定颜色：同一 id 每次刷新都是同一个色相。 */
function blockColor(id: string): string {
  let hash = 0
  for (let i = 0; i < id.length; i += 1) {
    hash = (hash * 31 + id.charCodeAt(i)) >>> 0
  }
  const hue = hash % 360
  const sat = 45 + ((hash >> 9) % 25)
  const light = 46 + ((hash >> 17) % 16)
  return `hsl(${hue} ${sat}% ${light}%)`
}

/** 把 HSL 转成给 Canvas 用的具体 rgb，便于做明暗分层。 */
function shade(color: string, delta: number): string {
  return color.replace(/(\d+(?:\.\d+)?)%\)$/, (_m, raw: string) => {
    const next = Math.max(0, Math.min(100, Number.parseFloat(raw) + delta))
    return `${next}%)`
  })
}

interface ViewTransform {
  zoom: number
  offsetX: number
  offsetY: number
}

interface Voxel {
  x: number
  y: number
  z: number
  id: string
}

export default function LabPage() {
  const { push, host } = useToasts()

  const [path, setPath] = useState('')
  const [preview, setPreview] = useState<ProjectionPreview | undefined>()
  const [loading, setLoading] = useState(false)
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState('')

  // ── 视图变换：滚轮缩放 + 左键拖拽平移，只存 ref 不进 state ──
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const viewRef = useRef<ViewTransform>({ zoom: 1, offsetX: 0, offsetY: 0 })
  const dragRef = useRef<{ x: number; y: number } | null>(null)
  const [dragging, setDragging] = useState(false)
  // 每次数据/尺寸变化都自增，作为 useEffect 的重绘信号
  const [drawToken, setDrawToken] = useState(0)

  const redraw = useCallback(() => setDrawToken((n) => n + 1), [])

  const load = async () => {
    const target = path.trim()
    if (target.length === 0) {
      push('info', '先填写投影文件的绝对路径。')
      return
    }
    setLoading(true)
    setError('')
    try {
      const result = await request<ProjectionPreview>('lab.load', { path: target })
      setPreview(result)
      // 换文件就回到默认视图，否则旧文件的偏移量会套到新模型上
      viewRef.current = { zoom: 1, offsetX: 0, offsetY: 0 }
      redraw()
      if (result.unsupported != null && result.unsupported.length > 0) {
        push('error', `不支持的投影格式：${result.unsupported}`)
      }
    } catch (e) {
      setError(String(e))
      push('error', `加载失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
  }

  const generateWorldEater = async () => {
    setGenerating(true)
    try {
      const result = await request<{ path: string }>('lab.worldEater', {
        length: 64,
        width: 16,
        layers: 3,
        spacing: 2,
        layerGap: 1,
        offset: 2
      })
      push('success', `世吞模板已生成：${result.path}`)
      setPath(result.path)
    } catch (e) {
      push('error', `生成失败：${String(e)}`)
    } finally {
      setGenerating(false)
    }
  }

  // ── 3D 预览：等距体素手绘 ────────────────────────────────────────────

  useEffect(() => {
    const canvas = canvasRef.current
    if (canvas == null || preview == null) return
    const blocks = preview.preview
    if (blocks.length === 0) return

    const ctx = canvas.getContext('2d')
    if (ctx == null) return

    // 按 devicePixelRatio 放大位图，避免高分屏上发虚
    const dpr = window.devicePixelRatio || 1
    const rect = canvas.getBoundingClientRect()
    const width = Math.max(1, Math.floor(rect.width))
    const height = Math.max(1, Math.floor(rect.height))
    if (canvas.width !== width * dpr || canvas.height !== height * dpr) {
      canvas.width = width * dpr
      canvas.height = height * dpr
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.clearRect(0, 0, width, height)

    // 等距投影：x 向右下，z 向左下，y 向上。单位边长随后乘缩放。
    const unit = 7
    const cos30 = Math.cos(Math.PI / 6)
    const sin30 = Math.sin(Math.PI / 6)
    const view = viewRef.current

    // 深度排序：x+y+z 越大越靠近观察者（右下前方），后画即盖在上面
    const sorted: Voxel[] = [...blocks].sort(
      (a, b) => a.x + a.y + a.z - (b.x + b.y + b.z)
    )

    const toScreen = (vx: number, vy: number, vz: number) => ({
      sx: width / 2 + (vx - vz) * cos30 * unit * view.zoom + view.offsetX,
      sy: height / 2 + (vx + vz) * sin30 * unit * view.zoom - vy * unit * view.zoom + view.offsetY
    })

    for (const b of sorted) {
      const color = blockColor(b.id)
      const step = unit * view.zoom
      const hw = cos30 * step

      // 顶面：以 (x,z) 为中心的菱形
      const top = toScreen(b.x, b.y + 1, b.z)
      ctx.beginPath()
      ctx.moveTo(top.sx, top.sy - sin30 * step)
      ctx.lineTo(top.sx + hw, top.sy)
      ctx.lineTo(top.sx, top.sy + sin30 * step)
      ctx.lineTo(top.sx - hw, top.sy)
      ctx.closePath()
      ctx.fillStyle = shade(color, 14)
      ctx.fill()

      // 右侧面：顶面右下角往下拉一个格高
      ctx.beginPath()
      ctx.moveTo(top.sx + hw, top.sy)
      ctx.lineTo(top.sx, top.sy + sin30 * step)
      ctx.lineTo(top.sx, top.sy + sin30 * step + step)
      ctx.lineTo(top.sx + hw, top.sy + step)
      ctx.closePath()
      ctx.fillStyle = shade(color, -18)
      ctx.fill()
    }
  }, [preview, drawToken])

  // 尺寸变化（窗口缩放 / 卡片回流）后重画一次
  useEffect(() => {
    const canvas = canvasRef.current
    if (canvas == null) return
    const observer = new ResizeObserver(() => redraw())
    observer.observe(canvas)
    return () => observer.disconnect()
  }, [redraw])

  const onWheel = (e: React.WheelEvent<HTMLCanvasElement>) => {
    const view = viewRef.current
    const factor = e.deltaY < 0 ? 1.12 : 1 / 1.12
    view.zoom = Math.max(0.25, Math.min(6, view.zoom * factor))
    e.preventDefault()
    redraw()
  }

  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    dragRef.current = { x: e.clientX, y: e.clientY }
    setDragging(true)
    e.currentTarget.setPointerCapture(e.pointerId)
  }

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragRef.current
    if (drag == null) return
    const view = viewRef.current
    view.offsetX += e.clientX - drag.x
    view.offsetY += e.clientY - drag.y
    dragRef.current = { x: e.clientX, y: e.clientY }
    redraw()
  }

  const onPointerUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    dragRef.current = null
    setDragging(false)
    if (e.currentTarget.hasPointerCapture(e.pointerId)) {
      e.currentTarget.releasePointerCapture(e.pointerId)
    }
  }

  const resetView = () => {
    viewRef.current = { zoom: 1, offsetX: 0, offsetY: 0 }
    redraw()
  }

  const voxels = preview?.preview ?? []
  const stats = (preview?.stats ?? []).slice(0, MAX_STATS_ROWS)
  const maxCount = stats.length > 0 ? Math.max(...stats.map((s) => s.count)) : 1

  return (
    <>
      <PageShell
        title="实验室"
        subtitle="投影工坊：解析 .litematic 并画等距体素预览；世吞生成器：直接产出分层 TNT 模板。"
        actions={
          <button
            className="btn btn-subtle"
            title="重新加载"
            onClick={() => {
              if (path.trim().length > 0) void load()
            }}
          >
            ↻
          </button>
        }
      >
        {loading && <SkeletonList rows={3} />}

        {error !== '' && (
          <div className="info info-danger" style={{ marginBottom: 12 }}>
            <span aria-hidden style={{ fontSize: 16 }}>✕</span>
            <div className="info-body" style={{ fontSize: 12 }}>
              {error}
            </div>
          </div>
        )}

        {/* ── 工具条 ── */}
        <div
          className="card"
          style={{
            display: 'flex',
            alignItems: 'flex-end',
            gap: 12,
            flexWrap: 'wrap',
            padding: '14px 16px',
            marginBottom: 14
          }}
        >
          <div style={{ flex: 1, minWidth: 280 }}>
            <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
              投影文件路径
            </div>
            <input
              className="input"
              style={{ width: '100%' }}
              value={path}
              placeholder="/home/user/.minecraft/schematics/build.litematic"
              onChange={(e) => setPath(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void load()
              }}
            />
          </div>
          <button
            className="btn btn-primary"
            disabled={loading}
            onClick={() => void load()}
          >
            ↻ {loading ? '解析中…' : '加载预览'}
          </button>
          <button
            className="btn"
            disabled={generating}
            onClick={() => void generateWorldEater()}
          >
            ▤ {generating ? '生成中…' : '生成世吞模板'}
          </button>
        </div>

        {preview != null && preview.unsupported != null && preview.unsupported.length > 0 && (
          <div className="info info-warning" style={{ marginBottom: 14 }}>
            <span aria-hidden style={{ fontSize: 16 }}>⚠</span>
            <div className="info-body t-body">
              不支持的投影格式：{preview.unsupported}
            </div>
          </div>
        )}

        {preview != null && (
          <>
            {/* ── 统计卡 ── */}
            <div className="card" style={{ marginBottom: 14, padding: '14px 16px' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <span className="t-strong">投影统计</span>
                <span className="t-cap truncate">{preview.path}</span>
              </div>
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))',
                  gap: 8,
                  marginTop: 10
                }}
              >
                {[
                  { label: '尺寸 X', value: `${preview.sizeX}` },
                  { label: '尺寸 Y', value: `${preview.sizeY}` },
                  { label: '尺寸 Z', value: `${preview.sizeZ}` },
                  { label: '方块总数', value: `${preview.totalBlocks}` },
                  { label: '方块种类', value: `${preview.distinctBlocks}` },
                  {
                    label: '预览采样',
                    value: `${voxels.length}${preview.previewTruncated ? ' ↓' : ''}`
                  }
                ].map((cell, index) => (
                  <div
                    key={cell.label}
                    style={{
                      padding: '10px 12px',
                      borderRadius: 'var(--r-card)',
                      background: 'var(--surface-subtle)',
                      border: '1px solid var(--stroke-subtle)',
                      animation: `rowIn var(--t-enter) var(--e-enter) both`,
                      animationDelay: `${index * 39}ms`
                    }}
                  >
                    <div className="t-meta" style={{ display: 'block' }}>
                      {cell.label}
                    </div>
                    <div className="t-strong">{cell.value}</div>
                  </div>
                ))}
              </div>
              {(preview.truncated || preview.previewTruncated) && (
                <div className="t-meta" style={{ display: 'block', marginTop: 8 }}>
                  {preview.truncated
                    ? '文件方块数超出解析上限，统计与预览均基于已读取的部分。'
                    : ''}
                  {preview.previewTruncated
                    ? '预览已按步长降采样，仅绘制部分方块（不是完整模型）。'
                    : ''}
                </div>
              )}
            </div>

            {/* ── 3D 预览卡 ── */}
            <div
              className="card"
              style={{
                display: 'flex',
                flexDirection: 'column',
                gap: 10,
                padding: '14px 16px',
                marginBottom: 14
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 16 }}>◫</span>
                <span className="t-strong">等距预览</span>
                <div style={{ flex: 1 }} />
                <button className="btn btn-subtle" onClick={resetView}>
                  ↻ 复位视图
                </button>
              </div>
              {/* 画布容器固定 420px：预览高度跟内容无关，切换文件时布局不跳 */}
              <div
                style={{
                  position: 'relative',
                  height: 420,
                  borderRadius: 'var(--r-card)',
                  overflow: 'hidden',
                  background:
                    'radial-gradient(120% 100% at 50% 0%, var(--surface-mica-a) 0%, var(--surface-window) 100%)',
                  border: '1px solid var(--stroke-subtle)'
                }}
              >
                {voxels.length === 0 ? (
                  <EmptyState
                    title="没有可预览的方块"
                    hint="这个投影是空的，或者采样后没有落到任何方块。"
                  />
                ) : (
                  <>
                    <canvas
                      ref={canvasRef}
                      style={{
                        display: 'block',
                        width: '100%',
                        height: '100%',
                        cursor: dragging ? 'grabbing' : 'grab',
                        touchAction: 'none'
                      }}
                      onWheel={onWheel}
                      onPointerDown={onPointerDown}
                      onPointerMove={onPointerMove}
                      onPointerUp={onPointerUp}
                      onPointerCancel={onPointerUp}
                    />
                    <div
                      style={{
                        position: 'absolute',
                        right: 10,
                        bottom: 10,
                        fontSize: 11,
                        opacity: 0.5,
                        pointerEvents: 'none',
                        background: 'var(--surface-layer)',
                        padding: '3px 8px',
                        borderRadius: 'var(--r-control)'
                      }}
                    >
                      滚轮缩放 · 左键拖拽平移
                    </div>
                  </>
                )}
              </div>
            </div>

            {/* ── 方块统计列表 ── */}
            <div className="card" style={{ padding: '14px 16px' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <span className="t-strong">方块种类</span>
                <span className="t-cap">({stats.length})</span>
                <div style={{ flex: 1 }} />
                {preview.stats.length > MAX_STATS_ROWS && (
                  <span className="t-meta">仅显示前 {MAX_STATS_ROWS} 条</span>
                )}
              </div>
              <div style={{ marginTop: 8 }}>
                {stats.length === 0 ? (
                  <EmptyState title="没有方块数据" hint="换一个投影文件再试试。" />
                ) : (
                  stats.map((s, index) => (
                    <div
                      key={s.id}
                      className="row row-static"
                      style={{
                        padding: '7px 10px',
                        margin: '0 4px',
                        animationDelay: `${index * 39}ms`
                      }}
                    >
                      {/* 色块：和画布里该方块的颜色一致，所见即所得 */}
                      <div
                        style={{
                          width: 18,
                          height: 18,
                          borderRadius: 'var(--r-control)',
                          flexShrink: 0,
                          border: '1px solid var(--stroke-strong)',
                          background: blockColor(s.id)
                        }}
                      />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div className="t-body truncate">{s.id}</div>
                        <div
                          style={{
                            height: 4,
                            borderRadius: 2,
                            background: 'var(--surface-hover)',
                            overflow: 'hidden',
                            marginTop: 4
                          }}
                        >
                          <div
                            style={{
                              width: `${(s.count / maxCount) * 100}%`,
                              height: '100%',
                              background: blockColor(s.id),
                              transition: 'width 450ms cubic-bezier(0.1,0.9,0.2,1)'
                            }}
                          />
                        </div>
                      </div>
                      <span
                        className="t-body"
                        style={{ opacity: 0.75, minWidth: 72, textAlign: 'right' }}
                      >
                        {s.count}
                      </span>
                    </div>
                  ))
                )}
              </div>
            </div>
          </>
        )}

        {!loading && preview == null && (
          <div className="card">
            <EmptyState
              title="还没有载入投影"
              hint="填一个 .litematic 的绝对路径点「加载预览」，或直接生成一份世吞模板试手气。"
              action={
                <button className="btn" onClick={() => void generateWorldEater()}>
                  ▤ 生成世吞模板
                </button>
              }
            />
          </div>
        )}
        {host}
      </PageShell>
    </>
  )
}
