/// 设置页：对齐 Win 端 SettingsPage —— 左侧分类卡（游戏 / Java / 下载与资源 / 通用 / 跨设备）
/// + 右侧分组卡片（标题带黄色竖条）+ 底部固定保存条。
import { useCallback, useEffect, useState } from 'react'

import { request } from '../bridge'
import type { SettingsSnapshot } from '../bridge'
import { SkeletonList, useToasts } from '../ui'
import { IconHome, IconCube, IconImport, IconSettings, IconLink } from '../icons'

type Section = 'game' | 'java' | 'download' | 'general' | 'crossdevice'

const SECTIONS: Array<{ id: Section; label: string; icon: React.ReactNode }> = [
  { id: 'game', label: '游戏', icon: <IconHome size={15} /> },
  { id: 'java', label: 'Java', icon: <IconCube size={15} /> },
  { id: 'download', label: '下载与资源', icon: <IconImport size={15} /> },
  { id: 'general', label: '通用', icon: <IconSettings size={15} /> },
  { id: 'crossdevice', label: '跨设备', icon: <IconLink size={15} /> }
]

/** 分组卡标题：左侧黄色竖条 + 标题（Win 端 SectionHeader 样式）。 */
function SectionTitle(props: { children: React.ReactNode }): JSX.Element {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
      <span
        style={{
          width: 4,
          height: 16,
          borderRadius: 2,
          background: 'var(--accent)',
          flexShrink: 0
        }}
      />
      <span className="t-strong" style={{ fontSize: 15 }}>
        {props.children}
      </span>
    </div>
  )
}

export default function SettingsPage(): JSX.Element {
  const { push, host } = useToasts()
  const [section, setSection] = useState<Section>('game')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)

  const [gameDir, setGameDir] = useState('')
  const [javaPath, setJavaPath] = useState('')
  /** 最大内存（GB，整数 —— 与 Win 端一致）。 */
  const [maxGb, setMaxGb] = useState(4)
  const [downloadSource, setDownloadSource] = useState('auto')
  const [javaProbe, setJavaProbe] = useState('')

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const s = await request<SettingsSnapshot>('settings.get')
      setGameDir(s.gameDir)
      setJavaPath(s.javaExecutable)
      setMaxGb(Math.max(1, Math.round(s.maxMemoryMb / 1024)))
      setDownloadSource(s.downloadSource)
    } catch (e) {
      push('error', `读取设置失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  async function probeJava(): Promise<void> {
    setJavaProbe('探测中…')
    try {
      const r = await request<{
        runtimes: Array<{ versionString: string; majorVersion: number }>
      }>('java.list')
      if (r.runtimes.length === 0) {
        setJavaProbe('未找到可用的 Java —— 请安装 JRE 17+ 或在上方指定路径')
      } else {
        setJavaProbe(
          '已发现：' +
            r.runtimes.map((j) => `${j.versionString}（Java ${j.majorVersion}）`).join('、')
        )
      }
    } catch (e) {
      setJavaProbe(`探测失败：${String(e)}`)
    }
  }

  async function save(): Promise<void> {
    setSaving(true)
    try {
      await request('settings.apply', {
        gameDir,
        javaExecutable: javaPath,
        maxMemoryMb: maxGb * 1024,
        downloadSource
      })
      push('success', '设置已保存')
    } catch (e) {
      push('error', `保存失败：${String(e)}`)
    } finally {
      setSaving(false)
    }
  }

  const sources: Array<{ id: string; label: string }> = [
    { id: 'auto', label: '自动（多源竞速）' },
    { id: 'official', label: '官方（piston-meta）' },
    { id: 'bmclapi', label: 'BMCLAPI 镜像' }
  ]

  return (
    <div className="page">
      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: 16 }}>
        {/* 左侧分类卡 */}
        <div className="card" style={{ width: 230, flexShrink: 0, padding: 8 }}>
          {SECTIONS.map((s) => (
            <button
              key={s.id}
              className={`nav-item${section === s.id ? ' nav-active' : ''}`}
              style={{ height: 40, width: '100%' }}
              onClick={() => setSection(s.id)}
            >
              <span className="nav-icon" style={{ fontFamily: 'inherit', fontSize: 14 }}>
                {s.icon}
              </span>
              <span>{s.label}</span>
            </button>
          ))}
        </div>

        {/* 右侧内容 */}
        <div
          style={{
            flex: 1,
            minWidth: 0,
            overflowY: 'auto',
            display: 'flex',
            flexDirection: 'column',
            gap: 14
          }}
        >
          {loading && <SkeletonList rows={3} />}

          {!loading && section === 'game' && (
            <>
              <div className="card">
                <SectionTitle>游戏目录</SectionTitle>
                <input
                  className="input"
                  style={{ width: '100%' }}
                  value={gameDir}
                  onChange={(e) => setGameDir(e.target.value)}
                />
                <div className="t-cap" style={{ marginTop: 8 }}>
                  版本、资源与库文件将安装到该目录
                </div>
              </div>

              <div className="card">
                <SectionTitle>内存分配</SectionTitle>
                <div className="t-body" style={{ marginBottom: 10 }}>
                  最大内存（整数 GB）
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
                  <input
                    type="range"
                    min={1}
                    max={16}
                    step={1}
                    value={maxGb}
                    onChange={(e) => setMaxGb(Number(e.target.value))}
                    style={{ flex: 1, accentColor: 'var(--accent)' }}
                  />
                  <span className="t-strong" style={{ width: 56, textAlign: 'right' }}>
                    {maxGb} GB
                  </span>
                </div>
                <div className="t-cap" style={{ marginTop: 10 }}>
                  最小内存固定 512 MB；过大的最大内存会让 GC 停顿变长。
                </div>
              </div>

              <div className="card">
                <SectionTitle>启动可靠性</SectionTitle>
                <div className="t-cap">
                  启动失败自动重试、启动页实时日志等选项由内核提供；当前版本接入后可用。
                </div>
              </div>
            </>
          )}

          {!loading && section === 'java' && (
            <div className="card">
              <SectionTitle>Java</SectionTitle>
              <div className="t-body" style={{ marginBottom: 8 }}>
                Java 可执行文件路径（留空 = 自动探测）
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <input
                  className="input"
                  style={{ flex: 1 }}
                  value={javaPath}
                  placeholder="例如 /usr/lib/jvm/java-17-openjdk/bin/java"
                  onChange={(e) => setJavaPath(e.target.value)}
                />
                <button className="btn" onClick={() => void probeJava()}>
                  探测
                </button>
              </div>
              {javaProbe !== '' && (
                <div className="t-cap" style={{ marginTop: 10 }}>
                  {javaProbe}
                </div>
              )}
            </div>
          )}

          {!loading && section === 'download' && (
            <div className="card">
              <SectionTitle>下载源</SectionTitle>
              <div
                className="seg"
                style={{
                  width: 460,
                  gridTemplateColumns: `repeat(${sources.length}, 1fr)`
                }}
              >
                <div
                  className="seg-ind"
                  style={{
                    width: `calc(${100 / sources.length}% - 2px)`,
                    // translateX 的百分比相对指示器自身宽度（= 一格）
                    transform: `translateX(calc(2px + ${
                      Math.max(0, sources.findIndex((s) => s.id === downloadSource)) * 100
                    }% + ${
                      Math.max(0, sources.findIndex((s) => s.id === downloadSource)) * 2
                    }px))`
                  }}
                />
                {sources.map((s) => (
                  <button
                    key={s.id}
                    className={`seg-item${s.id === downloadSource ? ' seg-active' : ''}`}
                    onClick={() => setDownloadSource(s.id)}
                  >
                    {s.label}
                  </button>
                ))}
              </div>
              <div className="t-cap" style={{ marginTop: 12 }}>
                自动模式会并发测速，选最快的源下载同一个文件。
              </div>
            </div>
          )}

          {!loading && section === 'general' && (
            <div className="card">
              <SectionTitle>通用</SectionTitle>
              <div className="t-cap">
                主题、语言、更新通道等通用选项由内核提供；当前版本接入后可用。
              </div>
            </div>
          )}

          {!loading && section === 'crossdevice' && (
            <div className="card">
              <SectionTitle>跨设备</SectionTitle>
              <div className="t-cap">
                登录 Starcloud ID 后可同步设置、投影与存档；当前未登录。
              </div>
            </div>
          )}
        </div>
      </div>

      {/* 底部固定保存条（Win 端同款） */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          marginTop: 14,
          padding: '10px 14px',
          borderRadius: 'var(--r-card)',
          background: 'var(--surface-subtle)',
          border: '1px solid var(--stroke-subtle)',
          flexShrink: 0
        }}
      >
        <span className="t-cap" style={{ flex: 1 }}>
          修改后在右侧保存；未保存的改动会在切换分类时保留。
        </span>
        <button className="btn btn-primary" disabled={saving || loading} onClick={() => void save()}>
          {saving ? '保存中…' : '保存设置'}
        </button>
      </div>

      {host}
    </div>
  )
}
