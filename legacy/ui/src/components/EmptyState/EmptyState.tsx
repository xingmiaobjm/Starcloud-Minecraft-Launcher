import type { ComponentChildren } from "preact";
import { PackageOpen } from "lucide";
import { Icon } from "../Icon/Icon";
import styles from "./EmptyState.module.css";

interface Props {
  icon?: ComponentChildren;
  title: string;
  desc?: string;
  /** 主行动（如「前往下载」按钮） */
  action?: ComponentChildren;
  /** 失败态样式（区别于普通空态） */
  error?: boolean;
  className?: string;
}

/** 统一空状态/失败状态：图标 + 标题 + 描述 + 行动按钮（未传图标时用默认图标） */
export function EmptyState({ icon, title, desc, action, error = false, className }: Props) {
  return (
    <div
      class={[
        styles.empty,
        error ? styles.error : "",
        className ?? "",
      ]
        .filter(Boolean)
        .join(" ")}
      role={error ? "alert" : undefined}
    >
      <div class={styles.icon} aria-hidden="true">
        {icon ?? <Icon icon={PackageOpen} size={22} strokeWidth={1.6} />}
      </div>
      <div class={styles.title}>{title}</div>
      {desc && <div class={styles.desc}>{desc}</div>}
      {action && <div class={styles.action}>{action}</div>}
    </div>
  );
}
