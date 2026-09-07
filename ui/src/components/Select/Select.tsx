import { useEffect, useRef, useState } from "preact/hooks";
import { ChevronDown } from "lucide";
import { Icon } from "../Icon/Icon";
import styles from "./Select.module.css";

export interface SelectOption {
  value: string;
  label: string;
}

interface SelectProps {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  placeholder?: string;
  title?: string;
  filterable?: boolean;
  /** aria-label（无可见标签时提供） */
  ariaLabel?: string;
  className?: string;
}

/**
 * Fluent 下拉选择：native 模式走系统 select；
 * filterable 模式为自定义 listbox，支持 ↑↓ 高亮 + Enter 选中 + Home/End。
 */
export function Select({
  value,
  options,
  onChange,
  placeholder = "请选择",
  title,
  filterable = false,
  ariaLabel,
  className,
}: SelectProps) {
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const [highlight, setHighlight] = useState(-1);
  const rootRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const current = options.find((o) => o.value === value);

  const close = () => {
    setOpen(false);
    setFilter("");
    setHighlight(-1);
  };

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        close();
      }
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
    filterable && kw ? options.filter((o) => o.label.toLowerCase().includes(kw)) : options;

  const pick = (v: string) => {
    onChange(v);
    close();
  };

  const onListKey = (e: KeyboardEvent) => {
    if (!open) return;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      setHighlight((h) => {
        const last = shown.length - 1;
        if (last < 0) return -1;
        const next = e.key === "ArrowDown" ? (h < 0 ? 0 : (h + 1) % (last + 1)) : (h <= 0 ? last : h - 1);
        // 高亮项滚动进可视区
        requestAnimationFrame(() => {
          listRef.current
            ?.querySelectorAll<HTMLButtonElement>("[role=option]")
            [next]?.scrollIntoView({ block: "nearest" });
        });
        return next;
      });
    } else if (e.key === "Home" && shown.length) {
      e.preventDefault();
      setHighlight(0);
    } else if (e.key === "End" && shown.length) {
      e.preventDefault();
      setHighlight(shown.length - 1);
    } else if (e.key === "Enter" && highlight >= 0 && shown[highlight]) {
      e.preventDefault();
      pick(shown[highlight].value);
    }
  };

  if (filterable) {
    return (
      <div class={`${styles.wrapper} ${className ?? ""}`} ref={rootRef} onKeyDown={onListKey}>
        <button
          type="button"
          class={`${styles.trigger} ${open ? styles.triggerOpen : ""}`}
          onClick={() => setOpen(!open)}
          aria-expanded={open}
          aria-haspopup="listbox"
          aria-label={ariaLabel}
          title={title}
        >
          <span class={styles.triggerLabel}>{current ? current.label : placeholder}</span>
          <Icon icon={ChevronDown} size={14} strokeWidth={1.8} class={styles.caret} />
        </button>
        {open && (
          <div class={styles.dropdown} role="listbox" aria-label={ariaLabel ?? title}>
            <input
              ref={searchRef}
              class={styles.search}
              placeholder="输入以筛选…"
              aria-label="筛选选项"
              value={filter}
              onInput={(e) => {
                setFilter((e.target as HTMLInputElement).value);
                setHighlight(-1);
              }}
            />
            <div class={styles.list} ref={listRef}>
              {shown.length === 0 && <div class={styles.empty}>无匹配项</div>}
              {shown.map((o, i) => (
                <button
                  type="button"
                  role="option"
                  aria-selected={o.value === value}
                  key={o.value}
                  class={[
                    styles.option,
                    o.value === value ? styles.optionSelected : "",
                    i === highlight ? styles.optionHighlight : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  onMouseEnter={() => setHighlight(i)}
                  onClick={() => pick(o.value)}
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

  return (
    <div class={`${styles.wrapper} ${className ?? ""}`}>
      <select
        class={styles.native}
        value={value}
        onChange={(e) => onChange((e.target as HTMLSelectElement).value)}
        aria-label={ariaLabel}
        title={title}
      >
        {!value && (
          <option value="" disabled>
            {placeholder}
          </option>
        )}
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
      <span class={styles.caretNative}>
        <Icon icon={ChevronDown} size={14} strokeWidth={1.8} />
      </span>
    </div>
  );
}
