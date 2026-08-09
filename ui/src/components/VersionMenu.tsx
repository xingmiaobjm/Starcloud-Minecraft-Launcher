// VersionMenu：版本选择按钮（启动按钮风格）+ 右侧滑入抽屉菜单

import { useEffect, useRef, useState } from "preact/hooks";
import type { VersionInfo } from "../types";

interface Props {
  versions: VersionInfo[];
  selected: string;
  loading: boolean;
  onSelect: (id: string) => void;
}

export function VersionMenu({ versions, selected, loading, onSelect }: Props) {
  const [open, setOpen] = useState(false);
  const openRef = useRef(false);
  const triggerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    openRef.current = open;
  }, [open]);

  const close = () => {
    if (!openRef.current) return;
    setOpen(false);
    triggerRef.current?.focus();
  };

  // Esc 关闭（点击遮罩由 backdrop 的 onClick 处理）
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        class={`version-trigger ${open ? "open" : ""}`}
        onClick={() => (open ? close() : setOpen(true))}
        aria-haspopup="dialog"
        aria-expanded={open}
      >
        <span class="version-trigger-label">
          {selected || (loading ? "加载中…" : "选择版本")}
        </span>
        <svg
          class="version-trigger-chevron"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2.2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>

      {open && (
        <>
          <div class="drawer-backdrop" onClick={close} />
          <div class="drawer" role="dialog" aria-label="版本列表">
            <div class="drawer-head">
              <span class="drawer-title">选择版本</span>
              <button class="drawer-close" onClick={close} aria-label="关闭">
                ×
              </button>
            </div>
            <div class="drawer-body">
              {versions.length === 0 && (
                <div class="menu-empty">{loading ? "加载中…" : "（未发现版本）"}</div>
              )}
              {versions.map((v) => (
                <button
                  key={v.id}
                  type="button"
                  class={`menu-item ${v.id === selected ? "active" : ""}`}
                  onClick={() => {
                    onSelect(v.id);
                    close();
                  }}
                >
                  <span class="menu-item-name">{v.id}</span>
                  {v.installed && <span class="tag installed">已装</span>}
                </button>
              ))}
            </div>
          </div>
        </>
      )}
    </>
  );
}
