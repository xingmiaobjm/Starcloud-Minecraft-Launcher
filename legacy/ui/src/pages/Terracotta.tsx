// 陶瓦联机页：基于 Terracotta 的 P2P 联机（创建房间 / 加入房间）
// 联机由 Terracotta | 陶瓦联机 提供技术支持

import { useEffect, useRef, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import { Check, Copy } from "lucide";
import {
  cancelInstall,
  listTasks,
  removeTask,
  terracottaInstall,
  terracottaJoinRoom,
  terracottaLogs,
  terracottaStartHost,
  terracottaStatus,
  terracottaStop,
} from "../api";
import type {
  InstallDonePayload,
  ProgressPayload,
  TerracottaStatePayload,
} from "../types";
import { friendlyError } from "../errorMessages";
import { Button } from "../components/Button/Button";
import { Card } from "../components/Card/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Icon } from "../components/Icon/Icon";
import { Input } from "../components/Input/Input";
import { MaybeNotice } from "../components/Notice/Notice";
import { Spinner } from "../components/Spinner/Spinner";
import { Tag } from "../components/Tag/Tag";
import { ProgressBar } from "../components/ProgressBar/ProgressBar";
import { useNotice } from "../hooks/useNotice";
import styles from "./Terracotta.module.css";

/** 联机核心安装任务键（进度事件 / install-done / 取消信号均使用） */
const CORE_JOB_KEY = "terracotta:core";

const STATE_LABELS: Record<string, string> = {
  waiting: "空闲",
  "host-scanning": "创建房间中（请先在游戏内开启局域网开放）",
  "host-starting": "房间启动中…",
  "host-ok": "房间已创建",
  "guest-connecting": "连接房间中…",
  "guest-starting": "已进入虚拟局域网",
  "guest-ok": "联机成功",
  exception: "联机异常",
};

const DIFFICULTY_LABELS: Record<string, string> = {
  easiest: "最简单",
  simple: "简单",
  medium: "中等",
  tough: "困难",
};

export function Terracotta() {
  const [info, setInfo] = useState<TerracottaStatePayload | null>(null);
  const [statusTimeout, setStatusTimeout] = useState(false);
  const [room, setRoom] = useState("");
  const [roomError, setRoomError] = useState<string | null>(null);
  const [installing, setInstalling] = useState(false);
  const [progress, setProgress] = useState<ProgressPayload | null>(null);
  const [busy, setBusy] = useState(false);
  const [logs, setLogs] = useState<string | null>(null);
  const [showLogs, setShowLogs] = useState(false);
  const [stopConfirm, setStopConfirm] = useState(false);
  const { notice, show, clear } = useNotice();

  // 复制房间邀请码后的短暂「已复制」反馈
  const [copied, setCopied] = useState(false);
  const copiedTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (copiedTimer.current) clearTimeout(copiedTimer.current);
    },
    [],
  );

  const refresh = () => {
    setStatusTimeout(false);
    terracottaStatus()
      .then(setInfo)
      .catch((e) => show({ text: friendlyError(e), kind: "err", raw: String(e) }));
  };

  /** 取消安装后清理调度中心遗留的失败任务记录 */
  const cleanupCancelledTask = async (detail: string) => {
    try {
      const tasks = await listTasks();
      const stale = tasks.find(
        (t) =>
          t.status === "error" &&
          t.title === "安装陶瓦联机核心" &&
          t.detail === detail,
      );
      if (stale) await removeTask(stale.id);
    } catch {
      /* 清理失败不影响主流程，记录可在调度中心手动删除 */
    }
  };

  // 订阅轮询事件 / 安装进度 / 安装完成
  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const unsubs: (() => void)[] = [];

    listen<TerracottaStatePayload>("terracotta-state", (e) => {
      setInfo(e.payload);
    }).then((u) => unsubs.push(u));

    listen<ProgressPayload>("download-progress", (e) => {
      if (e.payload.version_id === CORE_JOB_KEY) setProgress(e.payload);
    }).then((u) => unsubs.push(u));

    listen<InstallDonePayload>("install-done", (e) => {
      if (e.payload.job_id !== CORE_JOB_KEY) return;
      setInstalling(false);
      setProgress(null);
      if (e.payload.success) {
        show({ text: e.payload.message, kind: "ok" });
      } else if (e.payload.message.includes("取消")) {
        show("已取消安装");
        void cleanupCancelledTask(e.payload.message);
      } else {
        show({ text: friendlyError(e.payload.message), kind: "err", raw: e.payload.message });
      }
      refresh();
    }).then((u) => unsubs.push(u));

    refresh();
    return () => unsubs.forEach((u) => u());
  }, []);

  // 状态获取超时兜底：10 秒仍无响应时给出失败态（避免无限转圈）
  useEffect(() => {
    if (info !== null || statusTimeout) return;
    const t = window.setTimeout(() => setStatusTimeout(true), 10000);
    return () => window.clearTimeout(t);
  }, [info, statusTimeout]);

  const handleInstall = async () => {
    clear();
    setInstalling(true);
    try {
      await terracottaInstall();
    } catch (e) {
      setInstalling(false);
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    }
  };

  /** 取消安装：向后端发送取消信号（任务键 terracotta:core），完成后事件自动恢复状态 */
  const handleCancelInstall = async () => {
    try {
      await cancelInstall(CORE_JOB_KEY);
      show("正在取消安装…");
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    }
  };

  const handleHost = async () => {
    clear();
    setBusy(true);
    try {
      await terracottaStartHost();
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    } finally {
      setBusy(false);
    }
  };

  const handleJoin = async () => {
    const code = room.trim();
    if (!code) return;
    clear();
    setRoomError(null);
    setBusy(true);
    try {
      await terracottaJoinRoom(code);
    } catch (e) {
      const text = friendlyError(e);
      setRoomError(text);
      show({ text, kind: "err", raw: String(e) });
    } finally {
      setBusy(false);
    }
  };

  const doStop = async () => {
    setStopConfirm(false);
    clear();
    setBusy(true);
    try {
      await terracottaStop();
      refresh();
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    } finally {
      setBusy(false);
    }
  };

  const handleCopyRoom = async () => {
    const s = info?.state;
    if (!s || s.state !== "host-ok") return;
    try {
      await navigator.clipboard.writeText(s.room);
      // Copy → Check 形变 1.6s 后回切
      setCopied(true);
      if (copiedTimer.current) clearTimeout(copiedTimer.current);
      copiedTimer.current = setTimeout(() => setCopied(false), 1600);
      show({ text: "房间邀请码已复制，发给好友即可加入", kind: "ok" });
    } catch (e) {
      show({ text: "复制失败，请手动选择复制", kind: "err", raw: String(e) });
    }
  };

  /** 拉取日志（不改变面板开关状态）；失败仅提示，不把错误写进日志面板 */
  const loadLogs = async (): Promise<boolean> => {
    try {
      const text = await terracottaLogs();
      setLogs(text || "（暂无日志）");
      return true;
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
      return false;
    }
  };

  const handleToggleLogs = async () => {
    if (showLogs) {
      setShowLogs(false);
      return;
    }
    if (await loadLogs()) setShowLogs(true);
  };

  const st = info?.state ?? null;
  const stateKey = st ? st.state : null;

  function stateTagVariant(): "default" | "accent" | "success" | "warning" | "danger" {
    if (!stateKey) return "default";
    switch (stateKey) {
      case "host-ok":
      case "guest-ok":
        return "success";
      case "host-scanning":
      case "host-starting":
      case "guest-connecting":
      case "guest-starting":
        return "accent";
      case "exception":
        return "danger";
      default:
        return "default";
    }
  }

  return (
    <div class="page">
      <header class="page-header">
        <h1 class="page-title">陶瓦联机</h1>
        <p class="page-desc">
          无需公网 IP 或端口映射，与好友实时联机。由 Terracotta | 陶瓦联机
          提供技术支持（P2P 打洞，基于 EasyTier，为 Minecraft 优化）。
        </p>
      </header>

      <MaybeNotice notice={notice} onClose={clear} />

      {/* 状态卡片 */}
      <Card>
        {info === null ? (
          statusTimeout ? (
            <EmptyState
              error
              title="联机状态获取失败"
              desc="无法连接联机核心服务，请稍后重试"
              action={
                <Button variant="standard" onClick={refresh}>
                  重新加载
                </Button>
              }
            />
          ) : (
            <div class={styles.statusLoading} role="status">
              <Spinner size="sm" />
              <span>正在获取联机状态…</span>
            </div>
          )
        ) : (
          <div class={styles.statusGrid}>
            <div class={styles.statusItem}>
              <span class={styles.statusLabel}>联机核心</span>
              <Tag variant={info.installed ? "success" : "default"}>
                {info.installed ? `v${info.version}` : "未安装"}
              </Tag>
            </div>
            <div class={styles.statusItem}>
              <span class={styles.statusLabel}>核心会话</span>
              <Tag variant={info.running ? "success" : "default"}>
                {info.running ? "运行中" : "已停止"}
              </Tag>
            </div>
            {stateKey && (
              <div class={styles.statusItem}>
                <span class={styles.statusLabel}>联机状态</span>
                <Tag variant={stateTagVariant()}>
                  {STATE_LABELS[stateKey] ?? stateKey}
                </Tag>
              </div>
            )}
            {info.error && !stateKey && (
              <div class={styles.statusItem}>
                <span class={styles.statusLabel}>异常</span>
                <Tag variant="danger">{info.error}</Tag>
              </div>
            )}
          </div>
        )}

        {installing && !progress && (
          <div class={styles.statusLoading}>
            <Spinner size="sm" />
            <span>正在准备安装…</span>
          </div>
        )}

        {/* 安装进度 */}
        {installing && progress && (
          <div class={styles.progressWrap}>
            <ProgressBar
              percentage={progress.percentage}
              stage={progress.stage || "下载中…"}
              speedBps={progress.speed_bytes_per_sec > 0 ? progress.speed_bytes_per_sec : undefined}
              downloadedBytes={progress.downloaded_bytes}
              totalBytes={progress.total_bytes}
              etaSecs={progress.eta_secs}
            />
          </div>
        )}
      </Card>

      {/* 主操作区 */}
      {info && (
        <Card>
          {!info.installed ? (
            <>
              <p class={styles.installHint}>
                首次使用需下载联机核心（约 10~20 MB，国内镜像加速，可随时取消）。
                下载完成后即可创建或加入房间。
              </p>
              {installing ? (
                <Button variant="standard" onClick={handleCancelInstall}>
                  取消安装
                </Button>
              ) : (
                <Button onClick={handleInstall}>安装联机核心</Button>
              )}
            </>
          ) : !info.running ? (
            <div class={styles.actionsRow}>
              <Button onClick={handleHost} disabled={busy} busy={busy}>
                {busy ? "处理中…" : "创建房间"}
              </Button>
              <div class={styles.actionsDivider} />
              <Input
                className={styles.roomInput}
                placeholder="输入房主分享的房间邀请码…"
                hint="输入房主分享的房间码"
                error={roomError ?? undefined}
                value={room}
                onInput={(e) => {
                  setRoom((e.target as HTMLInputElement).value);
                  if (roomError) setRoomError(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter") handleJoin();
                }}
              />
              <Button variant="standard" onClick={handleJoin} disabled={busy || !room.trim()}>
                加入房间
              </Button>
              <Button variant="subtle" onClick={handleToggleLogs}>
                查看日志
              </Button>
            </div>
          ) : (
            <div class={styles.actionsStop}>
              <Button variant="danger" onClick={() => setStopConfirm(true)} disabled={busy}>
                停止联机
              </Button>
              <span class={styles.actionsHint}>
                {stateKey ? STATE_LABELS[stateKey] ?? stateKey : "联机核心运行中…"}
              </span>
            </div>
          )}
        </Card>
      )}

      {/* 联机状态详情 */}
      {st && st.state === "host-ok" && (
        <Card>
          <div class={styles.roomTitle}>房间已创建，把邀请码发给好友即可加入</div>
          <div class={styles.roomRow}>
            <code class={styles.roomCode}>{st.room}</code>
            <Button variant="standard" onClick={handleCopyRoom}>
              <Icon icon={copied ? Check : Copy} size={14} strokeWidth={1.6} />
              {copied ? "已复制" : "复制邀请码"}
            </Button>
          </div>
          <p class={styles.roomHint}>
            等待打洞完成后，好友在游戏内「多人游戏」的局域网世界列表中即可看到你的世界。
          </p>
        </Card>
      )}
      {st && st.state === "guest-starting" && (
        <Card>
          <div class={styles.stateTitle}>
            已进入虚拟局域网（P2P 难度：{DIFFICULTY_LABELS[st.difficulty] ?? st.difficulty}）
          </div>
          <p class={styles.stateHint}>
            现在可以在游戏内打开「多人游戏」，在局域网世界列表中应能看到房主的世界。
          </p>
        </Card>
      )}
      {st && st.state === "guest-ok" && (
        <Card>
          <div class={styles.stateTitle}>联机成功</div>
          <p class={styles.stateHint}>
            请在游戏内「多人游戏」→ 局域网世界列表中进入房主的世界。
          </p>
        </Card>
      )}
      {st && st.state === "exception" && (
        <Card>
          <div class={`${styles.stateTitle} ${styles.stateTitleErr}`}>
            联机异常（{st.type}）
          </div>
          <p class={styles.stateHint}>{st.message}</p>
        </Card>
      )}

      {/* 核心日志 */}
      {showLogs && (
        <Card>
          <div class={styles.logHead}>
            <span>联机核心日志</span>
            <div class={styles.logActions}>
              <Button variant="subtle" size="sm" onClick={() => void loadLogs()}>
                刷新
              </Button>
              <Button variant="subtle" size="sm" onClick={() => setShowLogs(false)}>
                关闭
              </Button>
            </div>
          </div>
          <pre class={styles.logBody}>{logs ?? "（暂无日志）"}</pre>
        </Card>
      )}

      {/* 使用指引 */}
      <Card>
        <div class={styles.guideTitle}>使用步骤</div>
        <ol class={styles.guideSteps}>
          <li>
            创建房间：先启动游戏并开启「对局域网开放」，再点击「创建房间」，等待状态变为「房间已创建」。
          </li>
          <li>
            加入房间：向房主要邀请码，粘贴后点击「加入房间」，状态变为「已进入虚拟局域网」后进入多人游戏即可看到房主世界。
          </li>
          <li>
            玩家名称使用当前默认账户；全部联机玩家建议接入同一地区节点以获得最佳延迟。
          </li>
        </ol>
      </Card>

      <p class={styles.brandLine}>联机由 Terracotta | 陶瓦联机 提供技术支持</p>

      {/* 停止联机确认（断开所有玩家） */}
      <ConfirmDialog
        open={stopConfirm}
        title="停止联机"
        message="确定要停止联机吗？所有玩家将断开连接。"
        confirmLabel="停止联机"
        onConfirm={doStop}
        onCancel={() => setStopConfirm(false)}
      />
    </div>
  );
}
