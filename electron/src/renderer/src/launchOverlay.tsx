/// 启动覆盖层（对齐 Win 端 HomePage 的 LaunchOverlay）：
/// 像素镐图标 → 版本名 → 进度条 → 当前步骤 / 登录方式 → 小贴士 → 显示日志 / 取消。
/// 覆盖整页背景，启动期间或启动失败时显示。
import { useEffect, useMemo, useRef, useState } from 'react'

import type { GameSessionApi } from './state/session'

/** 像素镐图标路径（与 Win 端 XAML 的 Path Data 完全一致）。 */
const PICKAXE_PATH =
  'M16,4 H32 V8 H36 V12 H40 V20 H36 V24 H32 V20 H28 V16 H24 V40 H20 V44 H16 V16 H12 V24 H8 V20 H12 V12 H16 Z'

/** 启动页小贴士静态池（与 Win 端 LaunchTips.All 同一份文案）。 */
const TIPS: string[] = [
  '右键版本列表里的版本，可以「问 Nova」或删除此版本。',
  'Nova 页可以开启游戏内浮窗，局内帮你盯帧数和崩溃。',
  '版本设置里可调内存分配，整合包一般 4-8G 就够。',
  '启动前的自检会自动补全缺失文件，不用手动下载。',
  'Nova 能帮你找整合包和模组，直接说需求就行。',
  '启动出错别慌，错误原因会直接写在启动页上。',
  '删错版本没关系，去下载页重新安装即可恢复。',
  '在「外置」模式填入你自己的第三方认证服务器即可登录。',
  '床可以记录重生点，但在下界会爆炸——可以当 TNT 用。',
  '铁傀儡不怕摔，但会溺水，别让它掉进水里。',
  '苦力怕怕猫：在家附近养猫可以吓退它。',
  '末影人碰水会受伤，也不会传送进水里。',
  '用命名牌在铁砧上改名的生物不会自然消失。',
  '蜂蜜块会减速并减轻摔落伤害，适合做缓冲垫。',
  '灵魂沙水下气泡会把你往上推，是天然电梯。',
  '村民交易会刷新职业等级，升级解锁更好的商品。',
  '内存并非越大越流畅，分配过多反而可能更卡。',
  'Java 版本会按整合包要求自动匹配，无需手动选。',
  '游戏崩溃时先看 .minecraft/logs 里的日志，看不懂问 Nova。',
  '窗口创建失败多半是显卡驱动问题，先更新驱动。',
  '装光影后启动失败，通常是驱动或内存不足导致。',
  '模组多时首次加载较慢，属于正常现象，耐心等待。',
  '大版本更新前记得备份存档，以防万一。',
  '整合包报「模组冲突」可以让 Nova 帮你定位。'
]

export default function LaunchOverlay(props: {
  session: GameSessionApi
  versionId?: string
  onCancel: () => void
}): JSX.Element | null {
  const s = props.session
  const [showLog, setShowLog] = useState(false)
  const logRef = useRef<HTMLDivElement | null>(null)

  // 每次进入启动流程换一条贴士（Win 端：窗口存活期内不重复）
  const tip = useMemo(() => TIPS[Math.floor(Math.random() * TIPS.length)], [s.launching])

  const failed = s.launchFailed === true && s.launching === false

  useEffect(() => {
    if (logRef.current !== null) logRef.current.scrollTop = logRef.current.scrollHeight
  }, [s.logs, showLog])

  if (!s.launching && !failed) return null

  const stage = s.stage ?? (failed ? '启动失败' : '正在启动')

  return (
    <div
      style={{
        position: 'absolute',
        inset: 0,
        zIndex: 500,
        background: 'var(--surface-window)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        animation: 'fadeIn 180ms cubic-bezier(0.33,0,0.67,1) both'
      }}
    >
      <div
        style={{
          width: 480,
          maxWidth: '92%',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          gap: 14
        }}
      >
        {/* 像素镐（品牌色） */}
        <svg width="48" height="48" viewBox="0 0 48 48" aria-hidden>
          <path d={PICKAXE_PATH} fill="var(--accent)" />
        </svg>

        {/* 版本名（覆盖层标题在 Win 端是「正在启动」，版本号在下方） */}
        <div style={{ fontSize: 24, fontWeight: 500, textAlign: 'center' }}>
          {failed ? '启动失败' : '正在启动'}
        </div>
        <div
          className="t-body truncate"
          style={{ maxWidth: '100%', textAlign: 'center', color: 'var(--text-secondary)' }}
          title={props.versionId}
        >
          {props.versionId ?? ''}
        </div>

        {/* 进度条 */}
        <div className="progress" style={{ width: 340, height: 4 }}>
          <div
            className="progress-fill"
            style={{ width: `${failed ? 100 : (s.progress ?? 6)}%` }}
          />
        </div>

        {/* 当前步骤 / 登录方式 */}
        <div
          style={{
            width: 340,
            display: 'grid',
            gridTemplateColumns: 'auto 1fr',
            rowGap: 4,
            columnGap: 8,
            fontSize: 13
          }}
        >
          <span style={{ opacity: 0.7, color: 'var(--text-secondary)' }}>当前步骤</span>
          <span className="truncate">{stage}</span>
          <span style={{ opacity: 0.7, color: 'var(--text-secondary)' }}>登录方式</span>
          <span className="truncate">{s.loginMode ?? '离线登录'}</span>
        </div>

        {/* 小贴士 / 失败原因 */}
        <div
          className="card"
          style={{ width: 340, padding: '10px 12px', display: 'flex', gap: 10 }}
        >
          <span
            style={{
              width: 4,
              borderRadius: 2,
              background: failed ? 'var(--danger)' : 'var(--accent)',
              flexShrink: 0
            }}
          />
          <span
            style={{
              fontSize: 12,
              lineHeight: '18px',
              color: 'var(--text-secondary)',
              whiteSpace: 'pre-wrap'
            }}
          >
            {failed ? s.status.replace(/^启动失败：/, '') : tip}
          </span>
        </div>

        {/* 显示日志 */}
        <button className="btn btn-sm" onClick={() => setShowLog((v) => !v)}>
          {showLog ? '隐藏日志' : '显示日志'}
        </button>
        {showLog && (
          <div
            ref={logRef}
            className="log"
            style={{
              width: 440,
              height: 180,
              background: 'var(--surface-subtle)',
              borderRadius: 8,
              border: '1px solid var(--stroke-subtle)'
            }}
          >
            {s.logs.length === 0 ? '（暂无输出）' : s.logs.join('\n')}
          </div>
        )}

        {/* 取消 / 关闭 */}
        <button
          className="btn"
          style={{ minWidth: 240 }}
          onClick={() => {
            setShowLog(false)
            props.onCancel()
          }}
        >
          {failed ? '关闭' : '取消'}
        </button>
      </div>
    </div>
  )
}
