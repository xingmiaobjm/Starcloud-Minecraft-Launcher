// 焦点陷阱：把 Tab 键循环限制在容器内（Modal / Drawer 共用）
// 用法：onKeyDown={(e) => trapTab(containerRef.current, e)}

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function trapTab(
  container: HTMLElement | null,
  e: KeyboardEvent,
): void {
  if (e.key !== "Tab" || !container) return;
  const focusables = Array.from(
    container.querySelectorAll<HTMLElement>(FOCUSABLE),
  ).filter((el) => el.offsetParent !== null || el === document.activeElement);
  if (focusables.length === 0) {
    e.preventDefault();
    return;
  }
  const first = focusables[0];
  const last = focusables[focusables.length - 1];
  const active = document.activeElement as HTMLElement | null;

  if (e.shiftKey) {
    if (active === first || !container.contains(active)) {
      e.preventDefault();
      last.focus();
    }
  } else if (active === last || !container.contains(active)) {
    e.preventDefault();
    first.focus();
  }
}

/** 获取容器内第一个可聚焦元素 */
export function firstFocusable(container: HTMLElement | null): HTMLElement | null {
  if (!container) return null;
  const el = container.querySelector<HTMLElement>(FOCUSABLE);
  return el && el.offsetParent !== null ? el : container;
}
