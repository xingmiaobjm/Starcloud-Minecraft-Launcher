// Select：自定义下拉选择器（替代原生 select，展开面板样式可控）
// - 点击展开/收起浮层；点击外部或 Esc 关闭
// - filterable 时顶部提供过滤输入（用于长列表如 MC 版本）

import { useEffect, useRef, useState } from "preact/hooks";

export interface SelectOption {
  value: string;
  label: string;
}

interface Props {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  placeholder?: string;
  /** 长列表（如版本）提供过滤输入框 */
  filterable?: boolean;
  title?: string;
}

export function Select({
  value,
  options,
  onChange,
  placeholder = "请选择",
  filterable = false,
  title,
}: Props) {
  const [open, setOpen] = useState(false);
  const [closing, setClosing] = useState(false);
  const [filter, setFilter] = useState("");
  const rootRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const closeTimer = useRef<number | null>(null);

  const current = options.find((o) => o.value === value);

  /** 收起（先播退场动画，动画结束后卸载浮层） */
  const close = () => {
    if (closeTimer.current) return;
    setClosing(true);
    closeTimer.current = window.setTimeout(() => {
      setOpen(false);
      setClosing(false);
      closeTimer.current = null;
    }, 200);
  };

  /** 展开（取消未完成的退场定时器） */
  const openPop = () => {
    if (closeTimer.current) {
      window.clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
    setClosing(false);
    setOpen(true);
  };

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    if (filterable) searchRef.current?.focus();
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open, filterable]);

  const kw = filter.trim().toLowerCase();
  const shown =
    filterable && kw
      ? options.filter((o) => o.label.toLowerCase().includes(kw))
      : options;

  return (
    <div class="cselect" ref={rootRef}>
      <button
        type="button"
        class={`cselect-btn ${open && !closing ? "open" : ""}`}
        onClick={() => (open ? close() : openPop())}
        title={title}
      >
        <span class="cselect-label">{current ? current.label : placeholder}</span>
        <svg
          class="cselect-caret"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="1.8"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>
      {open && (
        <div class={`cselect-pop ${closing ? "closing" : ""}`} role="listbox">
          {filterable && (
            <input
              ref={searchRef}
              class="cselect-search"
              placeholder="筛选…"
              value={filter}
              onInput={(e) => setFilter((e.target as HTMLInputElement).value)}
            />
          )}
          <div class="cselect-list">
            {shown.length === 0 && <div class="cselect-empty">无匹配项</div>}
            {shown.map((o) => (
              <button
                type="button"
                role="option"
                aria-selected={o.value === value}
                key={o.value}
                class={`cselect-item ${o.value === value ? "selected" : ""}`}
                onClick={() => {
                  onChange(o.value);
                  close();
                  setFilter("");
                }}
              >
                {o.label}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
