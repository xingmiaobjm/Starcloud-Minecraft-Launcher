import type { ComponentChildren } from "preact";
import styles from "./Tag.module.css";

type TagVariant = "default" | "accent" | "success" | "warning" | "danger";

interface Props {
  variant?: TagVariant;
  children: ComponentChildren;
  className?: string;
}

/** Fluent 信息徽标：状态信号专用（成功/警告/危险/强调） */
export function Tag({ variant = "default", children, className }: Props) {
  const classNames = [styles.tag, styles[variant], className].filter(Boolean).join(" ");

  return <span class={classNames}>{children}</span>;
}
