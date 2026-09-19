// 外观主题（浅色 / 深色 / 跟随系统）：localStorage 持久化 + 窗口毛玻璃联动
// "跟随系统"通过 prefers-color-scheme 媒体查询监听实时切换

import { setWindowTheme } from "./api";

const KEY = "launcher-theme";
const ACCENT_KEY = "launcher-accent";

export type Theme = "light" | "dark" | "system";

/**
 * 主题色预设（Win11 强调色板取色）：
 * - system = 跟随系统强调色（默认）
 * - 其余为固定色值，contrast 为该色上的文字/图形对比色
 */
export const ACCENT_PRESETS: { id: string; label: string; color: string; contrast: string }[] = [
  { id: "system", label: "跟随系统", color: "", contrast: "" },
  { id: "blue", label: "默认蓝", color: "#0067c0", contrast: "#ffffff" },
  { id: "violet", label: "粉紫", color: "#8b5cf6", contrast: "#ffffff" },
  { id: "teal", label: "青绿", color: "#038387", contrast: "#ffffff" },
  { id: "green", label: "草地绿", color: "#107c10", contrast: "#ffffff" },
  { id: "gold", label: "暖金", color: "#c19c00", contrast: "#1b1b1b" },
  { id: "orange", label: "暖橙", color: "#ca5010", contrast: "#ffffff" },
  { id: "red", label: "砖红", color: "#c42b1c", contrast: "#ffffff" },
  { id: "magenta", label: "洋红", color: "#c239b3", contrast: "#ffffff" },
];

/** 读取持久化主题色（null = 跟随系统） */
export function getAccentId(): string {
  return localStorage.getItem(ACCENT_KEY) || "system";
}

/** 应用主题色：写入 --accent-user 驱动 tokens.css 链路（system 时移除覆盖） */
export function applyAccent(id: string) {
  const preset = ACCENT_PRESETS.find((p) => p.id === id);
  const root = document.documentElement;
  if (!preset || !preset.color) {
    root.style.removeProperty("--accent-user");
    root.style.removeProperty("--accent-user-text");
    localStorage.removeItem(ACCENT_KEY);
  } else {
    root.style.setProperty("--accent-user", preset.color);
    root.style.setProperty("--accent-user-text", preset.contrast);
    localStorage.setItem(ACCENT_KEY, id);
  }
}

/** 启动时应用已保存主题色 */
export function initAccent() {
  applyAccent(getAccentId());
}

let mediaListener: ((e: MediaQueryListEvent) => void) | null = null;

/** 读取持久化主题（默认跟随系统） */
export function getTheme(): Theme {
  const v = localStorage.getItem(KEY);
  return v === "light" || v === "dark" ? v : "system";
}

/** 解析主题为实际外观（system 时查询系统偏好） */
export function resolveTheme(theme: Theme): "light" | "dark" {
  if (theme !== "system") return theme;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

/** 应用主题：写入 <html data-theme> 驱动 CSS，并同步窗口毛玻璃颜色 */
export function applyTheme(theme: Theme) {
  const resolved = resolveTheme(theme);
  document.documentElement.dataset.theme = resolved;
  localStorage.setItem(KEY, theme);
  setWindowTheme(resolved === "dark").catch(() => {});

  // system 模式挂监听：系统切换时实时更新（仅挂一次）
  if (theme === "system" && !mediaListener) {
    mediaListener = () => {
      if (getTheme() === "system") applyTheme("system");
    };
    window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", mediaListener);
  }
}

/** 启动时应用已保存主题 */
export function initTheme() {
  applyTheme(getTheme());
}
