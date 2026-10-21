/// 设置页：对齐 Win 端 SettingsPage —— 左侧分类卡 + 右侧分组卡片 + 底部保存条。
/// 字段全部落到 Starcloud.Core 的 LauncherSettings（与 Win 端共用一个 settings.json）。
import { useCallback, useEffect, useState } from 'react'

import { request } from '../bridge'
import type { SettingsSnapshot } from '../bridge'
import type { ThemeController, ThemeMode } from '../theme'
import { InfoBar, Segmented, Select, SkeletonList, Toggle, useToasts } from '../ui'
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
        style={{ width: 4, height: 16, borderRadius: 2, background: 'var(--accent)', flexShrink: 0 }}
      />
      <span className="t-strong" style={{ fontSize: 15 }}>
        {props.children}
      </span>
    </div>
  )
}

/** 一行「标签 + 说明 + 控件」。 */
function Field(props: {
  label: string
  hint?: string
  children: React.ReactNode
  /** 控件放右侧（开关类） */
  inline?: boolean
}): JSX.Element {
  if (props.inline === true) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 10 }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="t-body" style={{ color: 'var(--text-primary)' }}>
            {props.label}
          </div>
          {props.hint !== undefined && <div className="t-cap">{props.hint}</div>}
        </div>
        {props.children}
      </div>
    )
  }
  return (
    <div style={{ marginBottom: 12 }}>
      <div className="t-body" style={{ color: 'var(--text-primary)', marginBottom: 6 }}>
        {props.label}
      </div>
      {props.children}
      {props.hint !== undefined && (
        <div className="t-cap" style={{ marginTop: 6 }}>
          {props.hint}
        </div>
      )}
    </div>
  )
}

export default function SettingsPage(props: { theme: ThemeController }): JSX.Element {
  const { push, host } = useToasts()
  const [section, setSection] = useState<Section>('game')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)

  const [s, setS] = useState<SettingsSnapshot>({
    gameDir: '',
    javaExecutable: '',
    minMemoryMb: 512,
    maxMemoryMb: 4096,
    downloadSource: 'auto',
    downloadConcurrency: 16,
    downloadMaxRetries: 3,
    downloadCustomMirrorUrl: '',
    versionIsolated: true,
    useBuiltinJvmArgs: true,
    extraJvmArgs: [],
    language: 'zh_CN',
    theme: 'system',
    accountWalkAnimation: true,
    microsoftLoginEmbedded: true
  })
  const [jvmArgsText, setJvmArgsText] = useState('')
  const [javaProbe, setJavaProbe] = useState('')

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const snap = await request<SettingsSnapshot>('settings.get')
      setS(snap)
      setJvmArgsText((snap.extraJvmArgs ?? []).join('\n'))
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
      setJavaProbe(
        r.runtimes.length === 0
          ? '未找到可用的 Java —— 请安装 JRE 17+ 或在上方指定路径'
          : '已发现：' + r.runtimes.map((j) => `${j.versionString}（Java ${j.majorVersion}）`).join('、')
      )
    } catch (e) {
      setJavaProbe(`探测失败：${String(e)}`)
    }
  }

  async function save(): Promise<void> {
    setSaving(true)
    try {
      const snapshot = await request<SettingsSnapshot>('settings.apply', {
        gameDir: s.gameDir,
        javaExecutable: s.javaExecutable,
        minMemoryMb: s.minMemoryMb,
        maxMemoryMb: s.maxMemoryMb,
        downloadSource: s.downloadSource,
        downloadConcurrency: s.downloadConcurrency,
        downloadMaxRetries: s.downloadMaxRetries,
        downloadCustomMirrorUrl: s.downloadCustomMirrorUrl,
        versionIsolated: s.versionIsolated,
        useBuiltinJvmArgs: s.useBuiltinJvmArgs,
        extraJvmArgs: jvmArgsText
          .split('\n')
          .map((x) => x.trim())
          .filter((x) => x !== ''),
        language: s.language,
        theme: props.theme.mode,
        accountWalkAnimation: s.accountWalkAnimation,
        microsoftLoginEmbedded: s.microsoftLoginEmbedded
      })
      setS(snapshot)
      push('success', '设置已保存')
    } catch (e) {
      push('error', `保存失败：${String(e)}`)
    } finally {
      setSaving(false)
    }
  }

  const set = <K extends keyof SettingsSnapshot>(key: K, value: SettingsSnapshot[K]): void =>
    setS((prev) => ({ ...prev, [key]: value }))

  const sources = [
    { value: 'auto', label: '自动（多源竞速）' },
    { value: 'official', label: '官方（piston-meta）' },
    { value: 'bmclapi', label: 'BMCLAPI 镜像' },
    { value: 'fastmirror', label: 'FastMirror' },
    { value: 'rainyun', label: '雨云镜像' },
    { value: 'custom', label: '自定义镜像' }
  ]

  return (
    <div className="page">
      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: 16 }}>
        {/* 左侧分类卡 */}
        <div className="card" style={{ width: 230, flexShrink: 0, padding: 8 }}>
          {SECTIONS.map((item) => (
            <button
              key={item.id}
              className={`nav-item${section === item.id ? ' nav-active' : ''}`}
              style={{ height: 40, width: '100%' }}
              onClick={() => setSection(item.id)}
            >
              <span className="nav-icon" style={{ fontFamily: 'inherit' }}>
                {item.icon}
              </span>
              <span>{item.label}</span>
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
                <Field label=".minecraft 目录" hint="版本、资源与库文件将安装到该目录">
                  <input
                    className="input"
                    style={{ width: '100%' }}
                    value={s.gameDir}
                    onChange={(e) => set('gameDir', e.target.value)}
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>内存分配</SectionTitle>
                <Field label={`最大内存（${Math.round(s.maxMemoryMb / 1024)} GB）`}>
                  <input
                    type="range"
                    min={1}
                    max={16}
                    step={1}
                    value={Math.max(1, Math.round(s.maxMemoryMb / 1024))}
                    onChange={(e) => set('maxMemoryMb', Number(e.target.value) * 1024)}
                    style={{ width: '100%', accentColor: 'var(--accent)' }}
                  />
                </Field>
                <Field label={`最小内存（${s.minMemoryMb} MB）`}>
                  <input
                    type="range"
                    min={256}
                    max={4096}
                    step={256}
                    value={s.minMemoryMb}
                    onChange={(e) => set('minMemoryMb', Number(e.target.value))}
                    style={{ width: '100%', accentColor: 'var(--accent)' }}
                  />
                </Field>
                <div className="t-cap">
                  内存并非越大越流畅；整合包一般 4–8 GB 足够。
                </div>
              </div>

              <div className="card">
                <SectionTitle>实例</SectionTitle>
                <Field
                  label="版本隔离"
                  hint="每个版本使用独立的 gameDir（mods / 配置互不影响）"
                  inline
                >
                  <Toggle
                    checked={s.versionIsolated}
                    onChange={(v) => set('versionIsolated', v)}
                    label="版本隔离"
                  />
                </Field>
                <Field
                  label="账户形象走路动画"
                  hint="关闭后账户卡里的玩家形象保持站立"
                  inline
                >
                  <Toggle
                    checked={s.accountWalkAnimation}
                    onChange={(v) => set('accountWalkAnimation', v)}
                    label="走路动画"
                  />
                </Field>
              </div>
            </>
          )}

          {!loading && section === 'java' && (
            <>
              <div className="card">
                <SectionTitle>Java</SectionTitle>
                <Field label="Java 可执行文件路径" hint="留空 = 自动探测">
                  <div style={{ display: 'flex', gap: 8 }}>
                    <input
                      className="input"
                      style={{ flex: 1 }}
                      value={s.javaExecutable}
                      placeholder="例如 /usr/lib/jvm/java-17-openjdk/bin/java"
                      onChange={(e) => set('javaExecutable', e.target.value)}
                    />
                    <button className="btn" onClick={() => void probeJava()}>
                      探测
                    </button>
                  </div>
                </Field>
                {javaProbe !== '' && <div className="t-cap">{javaProbe}</div>}
              </div>

              <div className="card">
                <SectionTitle>JVM 参数</SectionTitle>
                <Field
                  label="使用内置 JVM 参数"
                  hint="关闭后完全使用下面的自定义参数（不推荐）"
                  inline
                >
                  <Toggle
                    checked={s.useBuiltinJvmArgs}
                    onChange={(v) => set('useBuiltinJvmArgs', v)}
                    label="内置 JVM 参数"
                  />
                </Field>
                <Field label="附加 JVM 参数" hint="每行一个，例如 -XX:+UseG1GC">
                  <textarea
                    className="input"
                    style={{ width: '100%', minHeight: 90, fontFamily: 'var(--font-mono)' }}
                    value={jvmArgsText}
                    onChange={(e) => setJvmArgsText(e.target.value)}
                  />
                </Field>
              </div>
            </>
          )}

          {!loading && section === 'download' && (
            <>
              <div className="card">
                <SectionTitle>下载源</SectionTitle>
                <Field label="源" hint="自动模式会并发测速，选最快的源下载同一个文件">
                  <Select
                    width={360}
                    value={s.downloadSource}
                    options={sources}
                    onChange={(v) => set('downloadSource', v)}
                  />
                </Field>
                <Field label="自定义镜像根 URL" hint="留空 = 不启用">
                  <input
                    className="input"
                    style={{ width: '100%' }}
                    value={s.downloadCustomMirrorUrl}
                    placeholder="https://mirror.example.com/"
                    onChange={(e) => set('downloadCustomMirrorUrl', e.target.value)}
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>下载行为</SectionTitle>
                <Field label={`并发分片数（${s.downloadConcurrency}）`} hint="1–64，过高可能被镜像限速">
                  <input
                    type="range"
                    min={1}
                    max={64}
                    step={1}
                    value={s.downloadConcurrency}
                    onChange={(e) => set('downloadConcurrency', Number(e.target.value))}
                    style={{ width: '100%', accentColor: 'var(--accent)' }}
                  />
                </Field>
                <Field label={`失败重试次数（${s.downloadMaxRetries}）`}>
                  <input
                    type="range"
                    min={0}
                    max={10}
                    step={1}
                    value={s.downloadMaxRetries}
                    onChange={(e) => set('downloadMaxRetries', Number(e.target.value))}
                    style={{ width: '100%', accentColor: 'var(--accent)' }}
                  />
                </Field>
              </div>
            </>
          )}

          {!loading && section === 'general' && (
            <>
              <div className="card">
                <SectionTitle>外观</SectionTitle>
                <Field label="主题" hint="跟随系统时会随操作系统亮暗自动切换">
                  <Segmented<ThemeMode>
                    value={props.theme.mode}
                    options={[
                      { value: 'system', label: '跟随系统' },
                      { value: 'light', label: '浅色' },
                      { value: 'dark', label: '深色' }
                    ]}
                    onChange={props.theme.setMode}
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>语言</SectionTitle>
                <Field label="界面语言">
                  <Select
                    width={220}
                    value={s.language}
                    options={[
                      { value: 'zh_CN', label: '简体中文' },
                      { value: 'en_US', label: 'English（跟随 Win 端）' }
                    ]}
                    onChange={(v) => set('language', v)}
                  />
                </Field>
                <div className="t-cap">语言包由内核提供；当前版本仅简体中文完整。</div>
              </div>

              <div className="card">
                <SectionTitle>登录方式</SectionTitle>
                <Field
                  label="使用内置微软登录窗口"
                  hint="关闭后改为「显示一次性验证码 + 打开系统浏览器」"
                  inline
                >
                  <Toggle
                    checked={s.microsoftLoginEmbedded}
                    onChange={(v) => set('microsoftLoginEmbedded', v)}
                    label="内置登录窗口"
                  />
                </Field>
              </div>
            </>
          )}

          {!loading && section === 'crossdevice' && (
            <>
              <div className="card">
                <SectionTitle>跨设备</SectionTitle>
                <InfoBar kind="info" title="需要 Starcloud ID">
                  登录后可在多台设备间同步设置、投影与存档。当前未登录。
                </InfoBar>
              </div>
            </>
          )}
        </div>
      </div>

      {/* 底部固定保存条 */}
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
          修改后点右侧保存；未保存的改动会在切换分类时保留。
        </span>
        <button
          className="btn btn-primary"
          disabled={saving || loading}
          onClick={() => void save()}
        >
          {saving ? '保存中…' : '保存设置'}
        </button>
      </div>

      {host}
    </div>
  )
}
