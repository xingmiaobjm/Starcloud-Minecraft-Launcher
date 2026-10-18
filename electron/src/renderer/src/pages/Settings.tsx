/// 设置页：游戏目录 / Java / 内存 / 下载源。
import { useEffect, useState } from 'react'
import {
  Button,
  Card,
  Dropdown,
  Field,
  Option,
  Slider,
  Input,
  Text,
  makeStyles
} from '@fluentui/react-components'
import { request } from '../bridge'
import type { SettingsSnapshot } from '../bridge'
import { brandYellow } from '../theme'
import { PageShell, useToasts } from '../ui'

const useStyles = makeStyles({
  section: { padding: '18px 20px', marginBottom: '12px' },
  row: { display: 'flex', alignItems: 'center', gap: '16px', marginTop: '12px' },
  field: { flex: 1 }
})

export default function SettingsPage() {
  const styles = useStyles()
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
        <Text>加载中…</Text>
        {host}
      </PageShell>
    )
  }

  return (
    <PageShell
      title="设置"
      subtitle="启动器全局配置（与 Windows 端共享同一份 launcher.settings.json）"
      actions={
        <Button
          appearance="primary"
          style={{ background: brandYellow, color: '#1b1b1b' }}
          disabled={saving}
          onClick={() => void save()}
        >
          {saving ? '保存中…' : '保存全部'}
        </Button>
      }
    >
      <Card className={styles.section}>
        <Text size={300} weight="semibold">
          游戏
        </Text>
        <div className={styles.row}>
          <Field label="游戏目录（.minecraft）" className={styles.field}>
            <Input value={gameDir} onChange={(_e, data) => setGameDir(data.value)} />
          </Field>
        </div>
        <div className={styles.row}>
          <Field label="游戏最大堆（MB）" className={styles.field}>
            <Slider
              min={1024}
              max={16384}
              step={512}
              value={memory}
              onChange={(_e, data) => setMemory(data.value)}
            />
          </Field>
          <Text weight="semibold" style={{ minWidth: '90px' }}>
            {memory} MB
          </Text>
        </div>
      </Card>

      <Card className={styles.section}>
        <Text size={300} weight="semibold">
          Java
        </Text>
        <div className={styles.row}>
          <Field label="Java 可执行文件路径（留空 = 自动探测）" className={styles.field}>
            <Input value={javaPath} onChange={(_e, data) => setJavaPath(data.value)} />
          </Field>
          <Button onClick={() => void probeJava()}>探测</Button>
        </div>
        {javaInfo !== '' && (
          <Text size={200} style={{ opacity: 0.75, marginTop: '8px' }}>
            {javaInfo}
          </Text>
        )}
      </Card>

      <Card className={styles.section}>
        <Text size={300} weight="semibold">
          下载
        </Text>
        <div className={styles.row}>
          <Field label="下载源" className={styles.field}>
            <Dropdown value={source} selectedOptions={[source]} onOptionSelect={(_e, data) => setSource(String(data.optionValue))}>
              <Option value="auto">自动（多源竞速）</Option>
              <Option value="official">官方（piston-meta）</Option>
              <Option value="bmclapi">BMCLAPI 镜像</Option>
            </Dropdown>
          </Field>
        </div>
      </Card>
      {host}
    </PageShell>
  )
}
