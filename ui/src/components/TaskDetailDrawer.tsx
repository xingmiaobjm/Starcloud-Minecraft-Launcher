// TaskDetailDrawer：任务详情面板（点击调度中心任务行打开）
// - 展示状态、发起来源、优先级、时间、进度、错误与日志
// - 可重试的任务提供「重试」（按任务标题推导后端调用）；
//   执行中可「取消任务」（移除记录），历史记录可「删除记录」，均经确认对话框

import { useEffect, useState } from "preact/hooks";
import type { TaskInfo } from "../types";
import {
  checkJava,
  downloadJavaRuntime,
  downloadServer,
  installFabric,
  installLoader,
  installVersion,
  removeTask,
  terracottaInstall,
} from "../api";
import { friendlyError } from "../errorMessages";
import { Button } from "./Button/Button";
import { ConfirmDialog } from "./ConfirmDialog";
import { Drawer } from "./Drawer/Drawer";
import { MaybeNotice } from "./Notice/Notice";
import { ProgressBar } from "./ProgressBar/ProgressBar";
import { useNotice } from "../hooks/useNotice";
import styles from "./TaskDetailDrawer.module.css";

interface Props {
  task: TaskInfo | null;
  onClose: () => void;
  /** 上移/下移调整优先级（调用方重排任务列表并持久化） */
  onMove?: (task: TaskInfo, dir: -1 | 1) => void;
  /** 当前任务能否上移/下移（首个 / 末个 / 执行中时不可） */
  canMoveUp?: boolean;
  canMoveDown?: boolean;
}

/** 任务类型文案 */
const KIND_LABEL: Record<TaskInfo["kind"], string> = {
  install: "版本安装",
  fabric: "Fabric 安装",
  download: "资源下载",
  launch: "游戏启动",
  java: "Java 检测",
};

/** 来源文案 */
const SOURCE_LABEL: Record<string, string> = {
  user: "用户发起",
  plugin: "插件发起",
  system: "系统内部",
};

/** 加载器显示名 → 加载器 id（与后端注册表对应，用于重试加载器安装任务） */
const LOADER_ID_BY_LABEL: Record<string, string> = {
  原版: "vanilla",
  Fabric: "fabric",
  Forge: "forge",
  Quilt: "quilt",
  NeoForge: "neoforge",
};

/**
 * 按任务类型与标题推导重试动作；参数不可推导时返回 null（不显示重试按钮）。
 * 任务标题由后端生成（如「安装 1.20.4」「为 1.20.1 安装 Fabric」），此处反向解析。
 */
function buildRetry(task: TaskInfo): (() => Promise<unknown>) | null {
  const title = task.title;
  if (task.kind === "install") {
    if (title === "安装陶瓦联机核心") return terracottaInstall;
    const loaderMatch = title.match(/^为 (.+) 安装 (.+)$/);
    const loaderId = loaderMatch ? LOADER_ID_BY_LABEL[loaderMatch[2]] : undefined;
    const mcVersion = loaderMatch?.[1];
    if (mcVersion && loaderId) return () => installLoader(mcVersion, loaderId);
    const versionMatch = title.match(/^安装 (.+)$/);
    return versionMatch ? () => installVersion(versionMatch[1]) : null;
  }
  if (task.kind === "fabric") {
    const m = title.match(/^为 (.+) 安装 Fabric$/);
    return m ? () => installFabric(m[1]) : null;
  }
  if (task.kind === "download") {
    const m = title.match(/^下载 (.+) 服务端$/);
    return m ? () => downloadServer(m[1]) : null;
  }
  if (task.kind === "java") {
    if (title === "检测系统 Java") return checkJava;
    const m = title.match(/^下载 Java (\d+)$/);
    return m ? () => downloadJavaRuntime(Number(m[1])) : null;
  }
  // launch（缺用户名）/ Mod 下载（参数不在任务上）：无法重试
  return null;
}

/** 状态文案 */
function statusLabel(status: TaskInfo["status"]): string {
  switch (status) {
    case "running":
      return "执行中";
    case "error":
      return "失败";
    default:
      return "完成";
  }
}

/** 时间格式化：完整日期时间 */
function fmtFullTime(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(
    d.getHours(),
  )}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** 任务进度块（进度条 + 阶段 + 速度/ETA） */
function ProgressBlock({ task }: { task: TaskInfo }) {
  if (task.status !== "running" || task.progress == null) return null;
  return (
    <section class={styles.section}>
      <div class={styles.sectionTitle}>下载进度</div>
      <ProgressBar
        percentage={task.progress}
        stage={task.stage || "下载中…"}
        speedBps={task.speed ?? 0}
        etaSecs={task.eta}
      />
    </section>
  );
}

/** 错误信息（仅失败任务展示；友好文案 + 原始详情折叠可复制） */
function ErrorBlock({ detail }: { detail: string | null | undefined }) {
  if (!detail) return null;
  return (
    <section class={styles.errorSection}>
      <div class={styles.sectionTitle}>错误信息</div>
      <p class={styles.errorText}>{friendlyError(detail)}</p>
      <details class={`${styles.errorRaw} selectable`}>
        <summary>详细信息</summary>
        <pre>{detail}</pre>
      </details>
    </section>
  );
}

/** 任务日志列表（倒序，最新在上） */
function LogBlock({ log }: { log: string[] }) {
  if (!log || log.length === 0) {
    return (
      <section class={styles.section}>
        <div class={styles.sectionTitle}>日志</div>
        <div class={styles.empty}>暂无日志</div>
      </section>
    );
  }
  const lines = [...log].reverse();
  return (
    <section class={styles.section}>
      <div class={styles.sectionTitle}>日志</div>
      <div class={`${styles.log} selectable`}>
        {lines.map((line, i) => (
          <div key={`${line}-${i}`} class={styles.logLine}>
            <span class={styles.logIndex}>{lines.length - i}</span>
            <span class={styles.logText}>{line}</span>
          </div>
        ))}
      </div>
    </section>
  );
}

export function TaskDetailDrawer({
  task,
  onClose,
  onMove,
  canMoveUp = false,
  canMoveDown = false,
}: Props) {
  const { notice, show, clear } = useNotice();
  /** 待确认操作：取消执行中任务 / 删除历史记录 */
  const [confirmAction, setConfirmAction] = useState<"cancel" | "delete" | null>(null);
  const [acting, setActing] = useState(false);

  // 切换任务时清掉上一任务的提示
  useEffect(() => {
    clear();
  }, [task?.id, clear]);

  if (!task) return null;

  const retry = buildRetry(task);
  const running = task.status === "running";

  const handleRetry = async () => {
    if (!retry) return;
    setActing(true);
    try {
      await retry();
      clear();
      onClose();
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    } finally {
      setActing(false);
    }
  };

  const handleConfirm = async () => {
    if (!confirmAction) return;
    setConfirmAction(null);
    try {
      await removeTask(task.id);
      onClose();
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    }
  };

  return (
    <Drawer open onClose={onClose} title="任务详情" width="440px">
      <div class={styles.head}>
        <span class={`${styles.statusBadge} ${styles[task.status] ?? ""}`}>
          {statusLabel(task.status)}
        </span>
        <span class={styles.title}>{task.title}</span>
      </div>

      <MaybeNotice notice={notice} onClose={clear} className={styles.notice} />

      <div class={styles.meta}>
        <div class={styles.metaItem}>
          <span class={styles.metaKey}>任务类型</span>
          <span class={styles.metaVal}>{KIND_LABEL[task.kind] ?? task.kind}</span>
        </div>
        <div class={styles.metaItem}>
          <span class={styles.metaKey}>当前优先级</span>
          <span class={styles.metaVal}>
            #{task.priority + 1}
            <span class={styles.metaHint}>（值 {task.priority}，0 最高）</span>
          </span>
        </div>
        <div class={styles.metaItem}>
          <span class={styles.metaKey}>发起来源</span>
          <span class={styles.metaVal}>{SOURCE_LABEL[task.source] ?? task.source}</span>
        </div>
        <div class={styles.metaItem}>
          <span class={styles.metaKey}>创建时间</span>
          <span class={styles.metaVal}>{fmtFullTime(task.created_at)}</span>
        </div>
        <div class={styles.metaItem}>
          <span class={styles.metaKey}>更新时间</span>
          <span class={styles.metaVal}>{fmtFullTime(task.updated_at)}</span>
        </div>
      </div>

      <ProgressBlock task={task} />
      {task.status === "error" && <ErrorBlock detail={task.detail} />}
      <LogBlock log={task.log} />

      <div class={styles.footer}>
        {onMove && (
          <div class={styles.moveRow}>
            <Button
              variant="subtle"
              size="sm"
              disabled={!canMoveUp}
              onClick={() => onMove(task, -1)}
            >
              上移
            </Button>
            <Button
              variant="subtle"
              size="sm"
              disabled={!canMoveDown}
              onClick={() => onMove(task, 1)}
            >
              下移
            </Button>
            <span class={styles.moveHint}>越靠前越先执行</span>
          </div>
        )}
        <div class={styles.actions}>
          {retry && !running && (
            <Button onClick={handleRetry} busy={acting}>
              重试任务
            </Button>
          )}
          {running ? (
            <Button variant="standard" onClick={() => setConfirmAction("cancel")}>
              取消任务
            </Button>
          ) : (
            <Button variant="danger" onClick={() => setConfirmAction("delete")}>
              删除记录
            </Button>
          )}
        </div>
      </div>

      <ConfirmDialog
        open={confirmAction !== null}
        title={confirmAction === "cancel" ? "取消任务" : "删除记录"}
        message={
          confirmAction === "cancel"
            ? "确定要取消该任务吗？任务将从调度中心移除。"
            : `确定要删除「${task.title}」的任务记录吗？`
        }
        confirmLabel={confirmAction === "cancel" ? "取消任务" : "删除"}
        danger={confirmAction === "delete"}
        onConfirm={handleConfirm}
        onCancel={() => setConfirmAction(null)}
      />
    </Drawer>
  );
}
