// ContextMenu：自定义右键菜单（替代 WebView 原生浏览器菜单）
// 支持 ↑↓ 键盘导航 + Enter 确认 + Home/End；打开时聚焦首项；Esc/失焦/滚动关闭
// 用法：目标元素 onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, items }); }}

import { useEffect, useRef } from "preact/hooks";
import type { IconInput } from "morphicons/element";
import { Icon } from "./Icon/Icon";
import styles from "./ContextMenu.module.css";

export interface ContextMenuItem {
  label: string;
  /** 图标数据：lucide IconNode（如 import { Trash2 } from "lucide"）或原始 d 字符串 */
  icon?: IconInput;
  /** 危险操作（删除等），红色语义 */
  danger?: boolean;
  disabled?: boolean;
  onClick: () => void;
}

interface Props {
  x: number;
  y: number;
  items: ContextMenuItem[];
  onClose: () => void;
}

export function ContextMenu({ x, y, items, onClose }: Props) {
  const ref = useRef<HTMLDivElement>(null);

  // 打开后钳制位置，避免菜单超出视口
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const { innerWidth, innerHeight } = window;
    const rect = el.getBoundingClientRect();
    const left = Math.min(x, Math.max(8, innerWidth - rect.width - 8));
    const top = Math.min(y, Math.max(8, innerHeight - rect.height - 8));
    el.style.left = `${left}px`;
    el.style.top = `${top}px`;
  }, [x, y]);

  // 键盘导航：↑↓ 移动高亮、Enter 确认、Esc 关闭；打开时聚焦首项
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const buttons = Array.from(
      el.querySelectorAll<HTMLButtonElement>("button:not(:disabled)"),
    );
    let idx = 0;
    buttons[0]?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        idx = e.key === "ArrowDown" ? (idx + 1) % buttons.length : (idx - 1 + buttons.length) % buttons.length;
        buttons[idx]?.focus();
      } else if (e.key === "Home") {
        e.preventDefault();
        idx = 0;
        buttons[0]?.focus();
      } else if (e.key === "End") {
        e.preventDefault();
        idx = buttons.length - 1;
        buttons[idx]?.focus();
      } else if (e.key === "Escape") {
        e.stopPropagation();
        onClose();
      }
    };
    document.addEventListener("keydown", onKey, true);
    return () => document.removeEventListener("keydown", onKey, true);
  }, [onClose]);

  // 点击菜单外 / 窗口失焦 / 滚动关闭
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    const onBlur = () => onClose();
    document.addEventListener("mousedown", onDown);
    window.addEventListener("blur", onBlur);
    // capture 阶段监听任意容器滚动（含页面内容区）
    window.addEventListener("wheel", onClose, { passive: true });
    return () => {
      document.removeEventListener("mousedown", onDown);
      window.removeEventListener("blur", onBlur);
      window.removeEventListener("wheel", onClose);
    };
  }, [onClose]);

  return (
    <div class={styles.menu} ref={ref} role="menu" style={{ left: x, top: y }}>
      {items.map((it) => (
        <button
          key={it.label}
          type="button"
          class={`${styles.item} ${it.danger ? styles.danger : ""}`}
          disabled={it.disabled}
          role="menuitem"
          onClick={() => {
            onClose();
            it.onClick();
          }}
        >
          {it.icon && (
            <span class={styles.icon} aria-hidden="true">
              <Icon icon={it.icon} size={16} strokeWidth={1.6} />
            </span>
          )}
          <span>{it.label}</span>
        </button>
      ))}
    </div>
  );
}
