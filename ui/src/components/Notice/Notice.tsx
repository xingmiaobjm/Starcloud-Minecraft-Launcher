import type { ComponentChildren } from "preact";
import { CircleAlert, CircleCheckBig, Info, X } from "lucide";
import { Icon } from "../Icon/Icon";
import styles from "./Notice.module.css";
import type { NoticeState } from "../../hooks/useNotice";

interface Props {
  notice: NoticeState;
  onClose?: () => void;
  /** 重试按钮回调（notice.onRetry 优先） */
  onRetry?: () => void;
  className?: string;
}

/**
 * 页面级提示条：成功（自动消失）/错误（常驻+可重试）/信息。
 * aria-live 播报；错误正文可选中复制（.selectable）。
 */
export function Notice({ notice, onClose, onRetry, className }: Props) {
  const retry = notice.onRetry ?? onRetry;
  const raw = notice.raw?.trim();

  return (
    <div
      class={[
        styles.notice,
        notice.kind === "ok" ? styles.ok : "",
        notice.kind === "err" ? styles.err : "",
        notice.kind === "info" ? styles.info : "",
        className ?? "",
      ]
        .filter(Boolean)
        .join(" ")}
      role={notice.kind === "err" ? "alert" : "status"}
      aria-live={notice.kind === "err" ? "assertive" : "polite"}
    >
      <span class={styles.icon} aria-hidden="true">
        {notice.kind === "ok" ? (
          <Icon icon={CircleCheckBig} size={16} strokeWidth={1.8} />
        ) : notice.kind === "err" ? (
          <Icon icon={CircleAlert} size={16} strokeWidth={1.8} />
        ) : (
          <Icon icon={Info} size={16} strokeWidth={1.8} />
        )}
      </span>
      <div class={styles.body}>
        <span class={`${styles.text} selectable`}>{notice.text}</span>
        {raw && raw !== notice.text && (
          <details class={`${styles.raw} selectable`}>
            <summary>详细信息</summary>
            <pre>{raw}</pre>
          </details>
        )}
      </div>
      {retry && (
        <button type="button" class={styles.retry} onClick={retry}>
          重试
        </button>
      )}
      {onClose && (
        <button type="button" class={styles.close} onClick={onClose} aria-label="关闭提示">
          <Icon icon={X} size={12} strokeWidth={1.5} />
        </button>
      )}
    </div>
  );
}

/** 便捷包装：notice 为 null 时不渲染 */
export function MaybeNotice({
  notice,
  onClose,
  onRetry,
  className,
}: {
  notice: NoticeState | null;
  onClose?: () => void;
  onRetry?: () => void;
  className?: string;
}): ComponentChildren {
  if (!notice) return null;
  return <Notice notice={notice} onClose={onClose} onRetry={onRetry} className={className} />;
}
