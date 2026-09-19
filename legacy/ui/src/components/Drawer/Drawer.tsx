import type { ComponentChildren } from "preact";
import { useEffect, useRef } from "preact/hooks";
import { X } from "lucide";
import { firstFocusable, trapTab } from "../focusTrap";
import { Icon } from "../Icon/Icon";
import styles from "./Drawer.module.css";

interface DrawerProps {
  open: boolean;
  onClose: () => void;
  title: string;
  children: ComponentChildren;
  width?: string;
  className?: string;
  /** 关闭按钮 id（供打开时自动聚焦） */
  closeId?: string;
  /** 侧栏内容（标题下方的操作区，可选） */
  actions?: ComponentChildren;
}

/**
 * 右侧滑出面板（Flyout）：Esc 内聚关闭、焦点圈定、
 * 关闭后焦点归还触发元素、打开时聚焦关闭按钮（或 closeId 指定元素）。
 */
export function Drawer({
  open,
  onClose,
  title,
  children,
  width = "420px",
  className,
  closeId,
  actions,
}: DrawerProps) {
  const closeRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const opener = document.activeElement as HTMLElement | null;
    const el = closeId ? document.getElementById(closeId) : closeRef.current;
    (el ?? firstFocusable(panelRef.current))?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onClose();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      opener?.focus?.();
    };
  }, [open, onClose, closeId]);

  if (!open) return null;

  return (
    <>
      <div class={styles.backdrop} onClick={onClose} />
      <div
        ref={panelRef}
        class={`${styles.drawer} ${className ?? ""}`}
        style={{ width }}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onKeyDown={(e) => trapTab(panelRef.current, e)}
      >
        <div class={styles.header}>
          <div class={styles.title}>{title}</div>
          {actions}
          <button
            ref={closeRef}
            id={closeId}
            class={styles.close}
            onClick={onClose}
            aria-label="关闭"
          >
            <Icon icon={X} size={14} strokeWidth={1.5} />
          </button>
        </div>
        <div class={styles.body}>{children}</div>
      </div>
    </>
  );
}
