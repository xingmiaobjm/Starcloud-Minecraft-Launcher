import type { ComponentChildren } from "preact";
import { useEffect, useRef } from "preact/hooks";
import { firstFocusable, trapTab } from "../focusTrap";
import styles from "./Modal.module.css";

interface ModalProps {
  open: boolean;
  onClose: () => void;
  title: string;
  children: ComponentChildren;
  width?: string;
  className?: string;
  /** 阻止 Esc / 遮罩点击关闭（如正在提交时） */
  dismissible?: boolean;
}

/**
 * Fluent ContentDialog：居中内容对话框。
 * role=dialog + aria-modal；Esc 与遮罩点击关闭；焦点圈定在对话框内。
 */
export function Modal({
  open,
  onClose,
  title,
  children,
  width = "420px",
  className,
  dismissible = true,
}: ModalProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const titleId = `dlg-${title.length}-${title.slice(0, 8)}`;

  // 打开：焦点移入对话框；Esc 关闭；关闭后焦点归还触发元素
  useEffect(() => {
    if (!open) return;
    const opener = document.activeElement as HTMLElement | null;
    firstFocusable(panelRef.current)?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && dismissible) {
        e.stopPropagation();
        onClose();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      opener?.focus?.();
    };
  }, [open, onClose, dismissible]);

  if (!open) return null;

  return (
    <div
      class={styles.overlay}
      onClick={dismissible ? onClose : undefined}
    >
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        class={`${styles.panel} ${className ?? ""}`}
        style={{ width }}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => trapTab(panelRef.current, e)}
      >
        <div class={styles.header}>
          <h2 class={styles.title} id={titleId}>
            {title}
          </h2>
        </div>
        <div class={styles.body}>{children}</div>
      </div>
    </div>
  );
}
