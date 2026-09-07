import { fmtBytes, fmtEta, fmtSpeed } from "../../utils/format";
import styles from "./ProgressBar.module.css";

interface ProgressBarProps {
  percentage: number;
  stage?: string;
  speedBps?: number;
  downloadedBytes?: number;
  totalBytes?: number;
  etaSecs?: number | null;
  /** aria-label（读屏描述，如「下载 1.20.4」） */
  label?: string;
  className?: string;
}

/** Fluent 进度条：3px 细轨道 + 信息行；progressbar 语义完整 */
export function ProgressBar({
  percentage,
  stage,
  speedBps,
  downloadedBytes,
  totalBytes,
  etaSecs,
  label,
  className,
}: ProgressBarProps) {
  const pct = Math.max(0, Math.min(100, percentage));
  const hasInfo =
    stage !== undefined ||
    speedBps !== undefined ||
    downloadedBytes !== undefined ||
    totalBytes !== undefined ||
    etaSecs !== undefined;

  return (
    <div
      class={`${styles.wrap} ${className ?? ""}`}
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(pct)}
      aria-label={label ?? stage ?? "进度"}
    >
      {hasInfo && (
        <div class={styles.infoRow}>
          {stage !== undefined && <span>{stage}</span>}
          <span>{pct.toFixed(0)}%</span>
        </div>
      )}
      <div class={styles.track}>
        <div class={styles.fill} style={{ width: `${pct}%` }} />
      </div>
      {(speedBps !== undefined || etaSecs !== undefined || (downloadedBytes !== undefined && totalBytes !== undefined)) && (
        <div class={styles.infoRowBelow}>
          <span>{speedBps !== undefined ? fmtSpeed(speedBps) : ""}</span>
          <span>{etaSecs !== undefined && etaSecs !== null ? fmtEta(etaSecs) : ""}</span>
          <span>
            {downloadedBytes !== undefined && totalBytes !== undefined
              ? `${fmtBytes(downloadedBytes)} / ${fmtBytes(totalBytes)}`
              : ""}
          </span>
        </div>
      )}
    </div>
  );
}
