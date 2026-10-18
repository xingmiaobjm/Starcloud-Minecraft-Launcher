/// 运行状态：当前会话状态 + 实时日志。
import { Card, Button, Text, makeStyles } from '@fluentui/react-components'
import { DeleteRegular } from '@fluentui/react-icons'
import { motion } from '../theme'
import { EmptyState } from '../ui'
import type { GameSessionApi } from '../state/session'

const useStyles = makeStyles({
  statusCard: { padding: '18px 20px', marginBottom: '12px' },
  logCard: { flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 },
  logBody: {
    flex: 1,
    overflowY: 'auto',
    padding: '10px 14px',
    fontFamily: 'Consolas, Menlo, monospace',
    fontSize: '11.5px',
    lineHeight: '1.5',
    whiteSpace: 'pre-wrap'
  },
  dot: { width: '9px', height: '9px', borderRadius: '50%' }
})

export default function RunningPage(props: { session: GameSessionApi }) {
  const styles = useStyles()
  const session = props.session
  const running = session.pid !== undefined && session.pid > 0

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <Card className={styles.statusCard}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
          <div
            className={styles.dot}
            style={{
              background: running ? '#3FBF6F' : '#8A8A8A',
              animation: running ? 'pulse 1.6s infinite' : undefined
            }}
          />
          <div style={{ flex: 1 }}>
            <Text size={400} weight="semibold">
              {session.status || '当前没有运行中的游戏'}
            </Text>
            <Text size={200} style={{ opacity: 0.6 }}>
              {running ? `进程 pid ${session.pid}` : '在首页选择版本与账户后启动游戏'}
            </Text>
          </div>
          {session.logs.length > 0 && (
            <Button appearance="subtle" icon={<DeleteRegular />} onClick={session.clearLogs}>
              清空日志
            </Button>
          )}
        </div>
      </Card>
      <Card className={styles.logCard}>
        {session.logs.length === 0 ? (
          <EmptyState title="暂无日志" hint="游戏启动后，这里会实时显示输出。" />
        ) : (
          <div className={styles.logBody}>
            {session.logs.map((line, index) => (
              <div
                key={index}
                style={{ animation: `fadeIn ${motion.fast}ms ${motion.ease} both` }}
              >
                {line}
              </div>
            ))}
          </div>
        )}
      </Card>
    </div>
  )
}
