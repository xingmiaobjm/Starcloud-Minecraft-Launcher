/// 设置页：游戏目录 / Java / 内存 / 下载源。
/// 视觉全部走 winui.css 的纯 CSS 类，不依赖 Fluent 组件。
import { useEffect, useState } from 'react'
import { request } from '../bridge'
import type { SettingsSnapshot } from '../bridge'
import { LoadingCard, PageShell, Segmented, useToasts } from '../ui'

const MEM_MIN = 1024
const MEM_MAX = 16384
const MEM_STEP = 512

const SOURCE_OPTIONS: Array<{ value: string; label: string }> = [
  { value: 'auto', label: '自动（多源竞速）' },
  { value: 'official', label: '官方（piston-meta）' },
  { value: 'bmclapi', label: 'BMCLAPI 镜像' }
]

export default function SettingsPage() {
  const { push, host } = useToasts()
  const [loading, setLoading] = useState(true)
  const [gameDir, setGameDir] = useState('')
  const [javaPath, setJavaPath] = useState('')
  const [memory, setMemory] = useState(4096)
  const [source, setSource] = useState('auto')
  const [saving, setSaving] = useState(false)
  const [javaInfo, setJavaInfo] = useState<string>('')

  async function reload(): Promise<void> {
    setLoading(true)
    try {
      const s = await request<SettingsSnapshot>('settings.get')
      setGameDir(s.gameDir)
      setJavaPath(s.javaExecutable ?? '')
      setMemory(s.maxMemoryMb)
      setSource(s.downloadSource)
    } catch (e) {
      push('error', `读取设置失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function probeJava(): Promise<void> {
    setJavaInfo('探测中…')
    try {
      const r = await request<{ runtimes: Array<{ versionString: string; majorVersion: number }> }>(
        'java.list'
      )
      if (r.runtimes.length === 0) {
        setJavaInfo('未找到可用的 Java —— 请安装 JRE 17+ 或在上方指定路径')
      } else {
        setJavaInfo(
          r.runtimes
            .slice(0, 3)
            .map((x) => `Java ${x.majorVersion}（${x.versionString}）`)
            .join(' · ')
        )
      }
    } catch (e) {
      setJavaInfo(`探测失败：${String(e)}`)
    }
  }

  async function save(): Promise<void> {
    setSaving(true)
    try {
      await request('settings.apply', {
        gameDir,
        javaExecutable: javaPath,
        maxMemoryMb: memory,
        downloadSource: source
      })
      push('success', '设置已保存并同步到 C# 核心')
      await reload()
    } catch (e) {
      push('error', `保存失败：${String(e)}`)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <PageShell title="设置" subtitle="启动器全局配置">
        <LoadingCard />
        {host}
      </PageShell>
    )
  }

  const memPct = ((memory - MEM_MIN) / (MEM_MAX - MEM_MIN)) * 100

  return (
    <>
      <PageShell
        title="设置"
        subtitle="启动器全局配置（与 Windows 端共享同一份 launcher.settings.json）"
        actions={
          <button className="btn btn-primary" disabled={saving} onClick={() => void save()}>
            {saving ? '保存中…' : '保存全部'}
          </button>
        }
      >
        <div className="card" style={{ marginBottom: 12 }}>
          <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 4 }}>游戏</div>
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, marginTop: 12 }}>
            <div style={{ flex: 1 }}>
              <div className="t-cap" style={{ marginBottom: 4 }}>
                游戏目录（.minecraft）
              </div>
              <input
                className="input"
                style={{ width: '100%' }}
                value={gameDir}
                onChange={(e) => setGameDir(e.target.value)}
              />
            </div>
          </div>
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, marginTop: 12 }}>
            <div style={{ flex: 1 }}>
              <div className="t-cap" style={{ marginBottom: 4 }}>
                游戏最大堆（MB）
              </div>
              <input
                type="range"
                min={MEM_MIN}
                max={MEM_MAX}
                step={MEM_STEP}
                value={memory}
                onChange={(e) => setMemory(Number(e.target.value))}
                style={{
                  width: '100%',
                  accentColor: 'var(--accent)',
                  background: `linear-gradient(to right, var(--accent) ${memPct}%, var(--surface-hover) ${memPct}%)`,
                  height: 4,
                  borderRadius: 2,
                  appearance: 'none',
                  outline: 'none'
                }}
              />
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  marginTop: 4
                }}
              >
                <span className="t-meta">{MEM_MIN} MB</span>
                <span className="t-meta">{MEM_MAX} MB</span>
              </div>
            </div>
            <span className="t-strong" style={{ minWidth: 90 }}>
              {memory} MB
            </span>
          </div>
        </div>

        <div className="card" style={{ marginBottom: 12 }}>
          <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 4 }}>Java</div>
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, marginTop: 12 }}>
            <div style={{ flex: 1 }}>
              <div className="t-cap" style={{ marginBottom: 4 }}>
                Java 可执行文件路径（留空 = 自动探测）
              </div>
              <input
                className="input"
                style={{ width: '100%' }}
                value={javaPath}
                onChange={(e) => setJavaPath(e.target.value)}
              />
            </div>
            <button className="btn" onClick={() => void probeJava()}>
              探测
            </button>
          </div>
          {javaInfo !== '' && (
            <div className="t-body" style={{ marginTop: 8 }}>
              {javaInfo}
            </div>
          )}
        </div>

        <div className="card">
          <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 4 }}>下载</div>
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, marginTop: 12 }}>
            <div style={{ flex: 1, maxWidth: 360 }}>
              <div className="t-cap" style={{ marginBottom: 4 }}>
                下载源
              </div>
              <Segmented
                value={source}
                options={SOURCE_OPTIONS}
                onChange={setSource}
              />
            </div>
          </div>
        </div>
      </PageShell>
      {host}
    </>
  )
}
