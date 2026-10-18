/// 启动器日志：显示桥接侧日志（game.output 等事件）与当前会话日志。
import { useEffect, useRef, useState } from 'react'
import { Button, Card, Text, makeStyles } from '@fluentui/react-components'
import { motion } from '../theme'
import { PageShell, EmptyState } from '../ui'

const useStyles = makeStyles({
  card: { flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 },
  body: {
    flex: 1,
    overflowY: 'auto',
    padding: '12px 16px',
    fontFamily: 'Consolas, Menlo, monospace',
    fontSize: '11.5px',
    lineHeight: '1.55',
    whiteSpace: 'pre-wrap'
  }
})

interface LogLine {
  id: number
  text: string
  time: string
}

export default function LogsPage() {
  const styles = useStyles()
  const [lines, setLines] = useState<LogLine[]>([])
  const seq = useRef(1)

  useEffect(() => {
    let alive = true
    void window.bridgeEvents.subscribe((event, params) => {
      if (!alive) return
      if (event === 'game.output') {
        setLines((prev) =>
          [
            ...prev,
            {
              id: seq.current++,
              text: String(params.line ?? ''),
              time: new Date().toLocaleTimeString('zh-CN')
            }
          ].slice(-500)
        )
      }
    })
    return () => {
      alive = false
    }
  }, [])

  // 会话日志也并入（同源：bridge 的 game.output 已经是唯一来源，这里兜底显示）
  const merged = lines

  return (
    <PageShell
      title="启动器日志"
      subtitle="桥接事件与游戏输出"
      actions={
        <Button appearance="subtle" onClick={() => setLines([])}>
          清空
        </Button>
      }
    >
      <Card className={styles.card}>
        {merged.length === 0 ? (
          <EmptyState title="暂无日志" hint="启动游戏后这里会实时滚动输出。" />
        ) : (
          <div className={styles.body}>
            {merged.map((line) => (
              <div key={line.id} style={{ animation: `fadeIn ${motion.fast}ms ${motion.ease} both` }}>
                {line.time !== '' && (
                  <span style={{ opacity: 0.45 }}>[{line.time}] </span>
                )}
                {line.text}
              </div>
            ))}
          </div>
        )}
      </Card>
    </PageShell>
  )
}
