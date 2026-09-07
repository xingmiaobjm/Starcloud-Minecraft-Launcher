import type { ComponentChildren, JSX } from "preact";
import styles from "./Card.module.css";

interface CardProps {
  children: ComponentChildren;
  className?: string;
  padding?: boolean;
  style?: JSX.CSSProperties;
  onClick?: (e: JSX.TargetedMouseEvent<HTMLDivElement>) => void;
  /** 可交互卡片：提供键盘可达性（Enter/Space 触发 onClick） */
  ariaLabel?: string;
}

/**
 * Fluent 卡片：白/深灰表面 + 1px 描边 + 8px 圆角。
 * 传 onClick 时自动获得 hover 抬升与键盘可达性。
 */
export function Card({
  children,
  className,
  padding = true,
  style,
  onClick,
  ariaLabel,
}: CardProps) {
  const interactive = onClick != null;

  const classNames = [
    styles.card,
    !padding ? styles.noPadding : "",
    interactive ? styles.interactive : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");

  if (interactive) {
    return (
      <div
        role="button"
        tabIndex={0}
        aria-label={ariaLabel}
        class={classNames}
        style={style}
        onClick={onClick}
        onKeyDown={(e: KeyboardEvent) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            onClick?.({} as never);
          }
        }}
      >
        {children}
      </div>
    );
  }

  return (
    <div class={classNames} style={style}>
      {children}
    </div>
  );
}
