import type { ComponentChildren } from "preact";
import { Spinner } from "../Spinner/Spinner";
import styles from "./Button.module.css";

type ButtonVariant = "primary" | "standard" | "subtle" | "danger";
type ButtonSize = "sm" | "md" | "lg";

interface Props {
  variant?: ButtonVariant;
  size?: ButtonSize;
  disabled?: boolean;
  busy?: boolean;
  children: ComponentChildren;
}

/**
 * Fluent 按钮：primary = 强调色填充（主行动，一屏至多一个）；
 * standard = 默认描边按钮；subtle = 幽灵按钮（工具栏/次要操作）；
 * danger = 危险填充（删除等不可逆操作）。
 */
export function Button({
  variant = "primary",
  size = "md",
  disabled = false,
  busy = false,
  className,
  children,
  ...rest
}: Props & Omit<import("preact").JSX.HTMLAttributes<HTMLButtonElement>, "size" | "disabled">) {
  const isDisabled = disabled || busy;

  const classNames = [
    styles.base,
    styles[size],
    styles[variant],
    busy ? styles.busy : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <button
      class={classNames}
      disabled={isDisabled}
      aria-busy={busy || undefined}
      {...rest}
    >
      {busy && <Spinner size={size === "lg" ? "md" : "sm"} />}
      {children}
    </button>
  );
}
