/// 设置页：与 Win 端 SettingsPage 逐项对齐。
/// 分类：游戏 / Java / 下载与资源 / 通用 / 跨设备。
/// 所有字段都落在 Starcloud.Core 的 LauncherSettings（与 Win 端共用 settings.json）。
import { useCallback, useEffect, useState } from 'react'

import { request } from '../bridge'
import type { SettingsSnapshot } from '../bridge'
import type { ThemeController, ThemeMode } from '../theme'
import { InfoBar, Segmented, Select, SkeletonList, Toggle, useToasts } from '../ui'
import { applyVisualSettings } from '../visualSettings'
import {
  IconClose,
  IconCube,
  IconFolder,
  IconHome,
  IconImport,
  IconLink,
  IconPlus,
  IconRefresh,
  IconSettings
} from '../icons'

type Section = 'game' | 'java' | 'download' | 'general' | 'crossdevice'

const SECTIONS: Array<{ id: Section; label: string; icon: React.ReactNode }> = [
  { id: 'game', label: '游戏', icon: <IconHome size={15} /> },
  { id: 'java', label: 'Java', icon: <IconCube size={15} /> },
  { id: 'download', label: '下载与资源', icon: <IconImport size={15} /> },
  { id: 'general', label: '通用', icon: <IconSettings size={15} /> },
  { id: 'crossdevice', label: '跨设备', icon: <IconLink size={15} /> }
]

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

function Field(props: {
  label: string
  hint?: string
  children: React.ReactNode
  inline?: boolean
}): JSX.Element {
  if (props.inline === true) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 12 }}>
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
    <div style={{ marginBottom: 14 }}>
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

function Slider(props: {
  value: number
  min: number
  max: number
  step?: number
  onChange: (v: number) => void
}): JSX.Element {
  const pct = ((props.value - props.min) / (props.max - props.min)) * 100
  return (
    <input
      type="range"
      min={props.min}
      max={props.max}
      step={props.step ?? 1}
      value={props.value}
      onChange={(e) => props.onChange(Number(e.target.value))}
      style={
        {
          width: '100%',
          '--fill': `${pct}%`
        } as React.CSSProperties
      }
    />
  )
}

const EMPTY: SettingsSnapshot = {
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
  microsoftLoginEmbedded: true,
  curseforgeApiKey: '',
  windowWidth: 854,
  windowHeight: 480,
  fullscreen: false,
  globalEnvVars: {},
  launchAutoRetry: true,
  showGameLogOnLaunch: false,
  novaOverlayEnabled: false,
  resourceCacheEnabled: true,
  motionLevel: '',
  backgroundMode: 'none',
  backgroundBuiltinId: '',
  backgroundImagePath: '',
  backgroundImageOpacity: 0.7,
  backgroundBlurRadius: 24,
  backgroundMaskOpacity: 0,
  backgroundFollowTheme: true,
  deviceLinkEnabled: false,
  deviceName: '',
  p2pDownloadEnabled: true,
  autoUpdateEnabled: true,
  updateManifestUrl: ''
}

export default function SettingsPage(props: { theme: ThemeController }): JSX.Element {
  const { push, host } = useToasts()
  const [section, setSection] = useState<Section>('game')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [s, setS] = useState<SettingsSnapshot>(EMPTY)
  const [jvmArgsText, setJvmArgsText] = useState('')
  const [javaProbe, setJavaProbe] = useState('')

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const snap = await request<SettingsSnapshot>('settings.get')
      setS({ ...EMPTY, ...snap })
      setJvmArgsText((snap.extraJvmArgs ?? []).join('\n'))
    } catch (e) {
      push('error', `读取设置失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  const set = <K extends keyof SettingsSnapshot>(key: K, value: SettingsSnapshot[K]): void =>
    setS((prev) => {
      const next = { ...prev, [key]: value }
      // 动效 / 背景类设置即时生效（不用等保存），便于当场看到效果
      applyVisualSettings(next)
      return next
    })

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
        microsoftLoginEmbedded: s.microsoftLoginEmbedded,
        curseforgeApiKey: s.curseforgeApiKey,
        windowWidth: s.windowWidth,
        windowHeight: s.windowHeight,
        fullscreen: s.fullscreen,
        globalEnvVars: s.globalEnvVars,
        launchAutoRetry: s.launchAutoRetry,
        showGameLogOnLaunch: s.showGameLogOnLaunch,
        novaOverlayEnabled: s.novaOverlayEnabled,
        resourceCacheEnabled: s.resourceCacheEnabled,
        motionLevel: s.motionLevel,
        backgroundMode: s.backgroundMode,
        backgroundBuiltinId: s.backgroundBuiltinId,
        backgroundImagePath: s.backgroundImagePath,
        backgroundImageOpacity: s.backgroundImageOpacity,
        backgroundBlurRadius: s.backgroundBlurRadius,
        backgroundMaskOpacity: s.backgroundMaskOpacity,
        backgroundFollowTheme: s.backgroundFollowTheme,
        deviceLinkEnabled: s.deviceLinkEnabled,
        deviceName: s.deviceName,
        p2pDownloadEnabled: s.p2pDownloadEnabled,
        autoUpdateEnabled: s.autoUpdateEnabled,
        updateManifestUrl: s.updateManifestUrl
      })
      setS({ ...EMPTY, ...snapshot })
      push('success', '设置已保存')
    } catch (e) {
      push('error', `保存失败：${String(e)}`)
    } finally {
      setSaving(false)
    }
  }

  const envEntries = Object.entries(s.globalEnvVars)

  return (
    <div className="page">
      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: 16 }}>
        {/* 左侧分类卡 */}
        <div className="card" style={{ width: 230, flexShrink: 0, padding: 8, alignSelf: 'flex-start' }}>
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

          {/* ══ 游戏 ══ */}
          {!loading && section === 'game' && (
            <>
              <div className="card">
                <SectionTitle>游戏目录</SectionTitle>
                <Field label=".minecraft 目录" hint="版本、资源与库文件将安装到该目录">
                  <div style={{ display: 'flex', gap: 8 }}>
                    <input
                      className="input"
                      style={{ flex: 1 }}
                      value={s.gameDir}
                      onChange={(e) => set('gameDir', e.target.value)}
                    />
                    <button
                      className="btn"
                      title="打开目录"
                      onClick={() =>
                        void request('shell.openPath', { path: s.gameDir }).catch((e) =>
                          push('error', String(e))
                        )
                      }
                    >
                      <IconFolder size={14} />
                    </button>
                  </div>
                </Field>
              </div>

              <div className="card">
                <SectionTitle>内存分配</SectionTitle>
                <Field label={`最大内存（${Math.round(s.maxMemoryMb / 1024)} GB）`}>
                  <Slider
                    min={1}
                    max={16}
                    value={Math.max(1, Math.round(s.maxMemoryMb / 1024))}
                    onChange={(v) => set('maxMemoryMb', v * 1024)}
                  />
                </Field>
                <Field label={`最小内存（${s.minMemoryMb} MB）`}>
                  <Slider
                    min={256}
                    max={4096}
                    step={256}
                    value={s.minMemoryMb}
                    onChange={(v) => set('minMemoryMb', v)}
                  />
                </Field>
                <div className="t-cap">内存并非越大越流畅；整合包一般 4–8 GB 足够。</div>
              </div>

              <div className="card">
                <SectionTitle>启动可靠性</SectionTitle>
                <Field
                  label="启动失败自动重试一次"
                  hint="游戏启动后 8 秒内异常退出时，自动重试一次（不重复环境体检）"
                  inline
                >
                  <Toggle
                    checked={s.launchAutoRetry}
                    onChange={(v) => set('launchAutoRetry', v)}
                    label="自动重试"
                  />
                </Field>
                <Field
                  label="启动页默认展开实时日志"
                  hint="启动时在启动页显示游戏实时输出，便于排查启动问题"
                  inline
                >
                  <Toggle
                    checked={s.showGameLogOnLaunch}
                    onChange={(v) => set('showGameLogOnLaunch', v)}
                    label="展开实时日志"
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>默认窗口</SectionTitle>
                <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end' }}>
                  <div style={{ width: 160 }}>
                    <Field label="窗口宽度">
                      <input
                        className="input"
                        style={{ width: '100%' }}
                        value={`${s.windowWidth}`}
                        onChange={(e) => set('windowWidth', Number(e.target.value) || 854)}
                      />
                    </Field>
                  </div>
                  <div style={{ width: 160 }}>
                    <Field label="窗口高度">
                      <input
                        className="input"
                        style={{ width: '100%' }}
                        value={`${s.windowHeight}`}
                        onChange={(e) => set('windowHeight', Number(e.target.value) || 480)}
                      />
                    </Field>
                  </div>
                  <div style={{ paddingBottom: 14 }}>
                    <Toggle
                      checked={s.fullscreen}
                      onChange={(v) => set('fullscreen', v)}
                      label="全屏启动"
                    />
                    <span className="t-body" style={{ marginLeft: 8 }}>
                      全屏启动
                    </span>
                  </div>
                </div>
              </div>

              <div className="card">
                <SectionTitle>启动参数</SectionTitle>
                <Field label="额外 JVM 参数（每行一条）" hint="例如 -XX:+UseG1GC">
                  <textarea
                    className="input"
                    style={{ width: '100%', minHeight: 90, fontFamily: 'var(--font-mono)' }}
                    value={jvmArgsText}
                    onChange={(e) => setJvmArgsText(e.target.value)}
                  />
                </Field>
                <Field
                  label="内置 JVM 参数"
                  hint="关闭后将不再自动附加官方推荐 JVM 参数"
                  inline
                >
                  <Toggle
                    checked={s.useBuiltinJvmArgs}
                    onChange={(v) => set('useBuiltinJvmArgs', v)}
                    label="内置 JVM 参数"
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>全局环境变量</SectionTitle>
                <div className="t-cap" style={{ marginBottom: 10 }}>
                  对所有版本生效；版本级同名变量会覆盖此处。
                </div>
                {envEntries.length === 0 && <div className="t-cap">还没有环境变量。</div>}
                {envEntries.map(([key, value]) => (
                  <div key={key} style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
                    <input className="input" style={{ width: 180 }} value={key} readOnly />
                    <input
                      className="input"
                      style={{ flex: 1 }}
                      value={value}
                      onChange={(e) =>
                        set('globalEnvVars', { ...s.globalEnvVars, [key]: e.target.value })
                      }
                    />
                    <button
                      className="btn btn-subtle btn-danger"
                      onClick={() => {
                        const next = { ...s.globalEnvVars }
                        delete next[key]
                        set('globalEnvVars', next)
                      }}
                    >
                      <IconClose size={14} />
                    </button>
                  </div>
                ))}
                <button
                  className="btn"
                  onClick={() => {
                    const name = `VAR_${envEntries.length + 1}`
                    set('globalEnvVars', { ...s.globalEnvVars, [name]: '' })
                  }}
                >
                  <IconPlus size={14} /> 添加环境变量
                </button>
              </div>

              <div className="card">
                <SectionTitle>版本隔离（默认）</SectionTitle>
                <div className="t-cap" style={{ marginBottom: 10 }}>
                  新版本的默认隔离方式。开启后只隔离 mods/存档/配置/资源包/光影，
                  库与资源文件仍共享；单个版本可在详情页覆盖。
                </div>
                <Segmented<'isolated' | 'shared'>
                  value={s.versionIsolated ? 'isolated' : 'shared'}
                  options={[
                    { value: 'isolated', label: '版本隔离（推荐）' },
                    { value: 'shared', label: '共享目录' }
                  ]}
                  onChange={(v) => set('versionIsolated', v === 'isolated')}
                />
              </div>
            </>
          )}

          {/* ══ Java ══ */}
          {!loading && section === 'java' && (
            <>
              <div className="card">
                <SectionTitle>Java</SectionTitle>
                <Field
                  label="Java 可执行文件路径"
                  hint="留空 = 自动探测；进入本页可点「搜索 Java」扫描本机运行时"
                >
                  <div style={{ display: 'flex', gap: 8 }}>
                    <input
                      className="input"
                      style={{ flex: 1 }}
                      value={s.javaExecutable}
                      placeholder="例如 /usr/lib/jvm/java-17-openjdk/bin/java"
                      onChange={(e) => set('javaExecutable', e.target.value)}
                    />
                    <button className="btn" onClick={() => void probeJava()}>
                      <IconRefresh size={14} /> 搜索 Java
                    </button>
                  </div>
                </Field>
                {javaProbe !== '' && <div className="t-cap">{javaProbe}</div>}
              </div>

              <div className="card">
                <SectionTitle>下载 Java</SectionTitle>
                <div className="t-cap" style={{ marginBottom: 10 }}>
                  Temurin 运行时下载由内核提供；当前版本请在系统里安装 JRE 17/21。
                </div>
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                  <button className="btn" disabled>
                    下载 Java 21（Temurin）
                  </button>
                  <button className="btn" disabled>
                    下载 Java 8（Temurin）
                  </button>
                  <button className="btn" disabled>
                    删除选中运行时
                  </button>
                </div>
              </div>
            </>
          )}

          {/* ══ 下载与资源 ══ */}
          {!loading && section === 'download' && (
            <>
              <div className="card">
                <SectionTitle>下载</SectionTitle>
                <Field label={`下载并发数（${s.downloadConcurrency}）`} hint="1–64，过高可能被镜像限速">
                  <Slider
                    min={1}
                    max={64}
                    value={s.downloadConcurrency}
                    onChange={(v) => set('downloadConcurrency', v)}
                  />
                </Field>
                <Field label={`失败重试次数（${s.downloadMaxRetries}）`}>
                  <Slider
                    min={0}
                    max={10}
                    value={s.downloadMaxRetries}
                    onChange={(v) => set('downloadMaxRetries', v)}
                  />
                </Field>
                <Field label="下载源" hint="自动模式会并发测速，选最快的源下载同一个文件">
                  <Select
                    width={360}
                    value={s.downloadSource}
                    options={[
                      { value: 'auto', label: '自动（多源聚合）' },
                      { value: 'official', label: '仅官方' },
                      { value: 'bmclapi', label: 'BMCLAPI' },
                      { value: 'fastmirror', label: 'FastMinecraftMirror' },
                      { value: 'rainyun', label: 'RainYun' },
                      { value: 'custom', label: '自定义镜像地址' }
                    ]}
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
                <Field
                  label="CurseForge API Key（可空）"
                  hint="留空走社区只读代理；填了就直连官方 api.curseforge.com"
                >
                  <input
                    className="input"
                    style={{ width: '100%' }}
                    value={s.curseforgeApiKey}
                    onChange={(e) => set('curseforgeApiKey', e.target.value)}
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>资源列表与图标缓存</SectionTitle>
                <Field
                  label="启用资源列表拉取缓存"
                  hint="打开资源页时先用缓存瞬时展示，后台拉到新内容后替换；关闭后完全不读不写缓存"
                  inline
                >
                  <Toggle
                    checked={s.resourceCacheEnabled}
                    onChange={(v) => set('resourceCacheEnabled', v)}
                    label="资源缓存"
                  />
                </Field>
                <button
                  className="btn"
                  onClick={() => push('info', '清空缓存：内核能力接入后可用')}
                >
                  清空缓存
                </button>
              </div>
            </>
          )}

          {/* ══ 通用 ══ */}
          {!loading && section === 'general' && (
            <>
              <div className="card">
                <SectionTitle>主题</SectionTitle>
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
                <Field label="界面语言">
                  <Select
                    width={240}
                    value={s.language}
                    options={[
                      { value: 'zh_CN', label: '简体中文' },
                      { value: 'en_US', label: 'English' }
                    ]}
                    onChange={(v) => set('language', v)}
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>性能与动效</SectionTitle>
                <Field
                  label="动效档位"
                  hint="极少：只保留页面转场；较少：+ 悬停反馈；均衡：+ 跟手光斑；全部：+ 弹簧形变"
                >
                  <Segmented<string>
                    value={s.motionLevel === '' ? 'balanced' : s.motionLevel}
                    options={[
                      { value: 'minimal', label: '极少' },
                      { value: 'lite', label: '较少' },
                      { value: 'balanced', label: '均衡' },
                      { value: 'full', label: '全部' }
                    ]}
                    onChange={(v) => set('motionLevel', v)}
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>背景与外观</SectionTitle>
                <Field label="背景">
                  <Select
                    width={280}
                    value={s.backgroundMode}
                    options={[
                      { value: 'none', label: '关闭（纯毛玻璃）' },
                      { value: 'builtin', label: '内置壁纸' },
                      { value: 'image', label: '本地图片' }
                    ]}
                    onChange={(v) => set('backgroundMode', v)}
                  />
                </Field>
                {s.backgroundMode === 'image' && (
                  <Field label="图片文件" hint="填本地图片的绝对路径">
                    <input
                      className="input"
                      style={{ width: '100%' }}
                      value={s.backgroundImagePath}
                      placeholder="/home/user/Pictures/bg.png"
                      onChange={(e) => set('backgroundImagePath', e.target.value)}
                    />
                  </Field>
                )}
                <Field label={`模糊强度（${Math.round(s.backgroundBlurRadius)}）`}>
                  <Slider
                    min={0}
                    max={64}
                    value={Math.round(s.backgroundBlurRadius)}
                    onChange={(v) => set('backgroundBlurRadius', v)}
                  />
                </Field>
                <Field label={`不透明度（${Math.round(s.backgroundImageOpacity * 100)}%）`}>
                  <Slider
                    min={0}
                    max={100}
                    value={Math.round(s.backgroundImageOpacity * 100)}
                    onChange={(v) => set('backgroundImageOpacity', v / 100)}
                  />
                </Field>
                <Field
                  label="背景跟随主题"
                  hint="深色主题下自动压暗背景"
                  inline
                >
                  <Toggle
                    checked={s.backgroundFollowTheme}
                    onChange={(v) => set('backgroundFollowTheme', v)}
                    label="背景跟随主题"
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>账户与登录</SectionTitle>
                <Field
                  label="账户页走路动画"
                  hint="关闭后，账户页右侧的玩家形象将保持站立姿势"
                  inline
                >
                  <Toggle
                    checked={s.accountWalkAnimation}
                    onChange={(v) => set('accountWalkAnimation', v)}
                    label="走路动画"
                  />
                </Field>
                <Field
                  label="使用内置登录窗口"
                  hint="微软登录在启动器内完成；关闭后改为显示验证码并打开浏览器"
                  inline
                >
                  <Toggle
                    checked={s.microsoftLoginEmbedded}
                    onChange={(v) => set('microsoftLoginEmbedded', v)}
                    label="内置登录窗口"
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>Nova 游戏内浮窗</SectionTitle>
                <Field
                  label="显示 Nova 浮窗"
                  hint="游戏运行时在屏幕角落显示浮窗，Ctrl+N 随时呼出对话；下局生效"
                  inline
                >
                  <Toggle
                    checked={s.novaOverlayEnabled}
                    onChange={(v) => set('novaOverlayEnabled', v)}
                    label="Nova 浮窗"
                  />
                </Field>
              </div>

              <div className="card">
                <SectionTitle>软件更新</SectionTitle>
                <Field
                  label="自动检查更新"
                  hint="启动几秒后后台检查新版本，发现时询问；游戏运行期间不打扰"
                  inline
                >
                  <Toggle
                    checked={s.autoUpdateEnabled}
                    onChange={(v) => set('autoUpdateEnabled', v)}
                    label="自动检查更新"
                  />
                </Field>
                <button className="btn" onClick={() => push('info', '检查更新：内核能力接入后可用')}>
                  检查更新
                </button>
              </div>
            </>
          )}

          {/* ══ 跨设备 ══ */}
          {!loading && section === 'crossdevice' && (
            <>
              <div className="card">
                <SectionTitle>跨设备与 P2P</SectionTitle>
                <Field
                  label="跨设备存档同步"
                  hint="同一 Starcloud ID 的设备在局域网内互相可见，可把世界与版本配置推送到其他设备"
                  inline
                >
                  <Toggle
                    checked={s.deviceLinkEnabled}
                    onChange={(v) => set('deviceLinkEnabled', v)}
                    label="跨设备存档同步"
                  />
                </Field>
                <Field label="设备名称" hint="留空 = 使用主机名">
                  <input
                    className="input"
                    style={{ width: 320 }}
                    value={s.deviceName}
                    onChange={(e) => set('deviceName', e.target.value)}
                  />
                </Field>
                <Field
                  label="P2P 下载加速"
                  hint="在局域网内从同账号设备下载已缓存的文件"
                  inline
                >
                  <Toggle
                    checked={s.p2pDownloadEnabled}
                    onChange={(v) => set('p2pDownloadEnabled', v)}
                    label="P2P 下载加速"
                  />
                </Field>
                <InfoBar kind="info" title="需要 Starcloud ID">
                  同步与 P2P 加速需要登录 Starcloud ID。
                </InfoBar>
                <div style={{ marginTop: 10 }}>
                  <button
                    className="btn btn-primary"
                    onClick={() => push('info', 'Starcloud ID 登录：内核能力接入后可用')}
                  >
                    去登录
                  </button>
                </div>
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
