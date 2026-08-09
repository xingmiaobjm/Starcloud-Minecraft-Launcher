// 应用外壳：侧边导航 + 状态栏 + 页面容器 + 全局事件订阅

import { useEffect, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { getGuiPlugins } from "./api";
import type {
  AppStatus,
  ErrorPayload,
  GameExitedPayload,
  GameLogPayload,
  GuiPluginInfo,
  InstallDonePayload,
  ProgressPayload,
  StatusPayload,
} from "./types";

import { Home } from "./pages/Home";
import { Versions } from "./pages/Versions";
import { Accounts } from "./pages/Accounts";
import { Settings } from "./pages/Settings";
import { Plugins } from "./pages/Plugins";
import { Logs } from "./pages/Logs";
import { PluginView } from "./pages/PluginView";

type PageId = "home" | "versions" | "accounts" | "settings" | "plugins" | "logs" | `view:${string}`;

const NAV: { id: PageId; icon: string; label: string }[] = [
  { id: "home", icon: "home", label: "首页" },
  { id: "versions", icon: "cube", label: "版本" },
  { id: "accounts", icon: "user", label: "账户" },
  { id: "settings", icon: "sliders", label: "设置" },
  { id: "plugins", icon: "grid", label: "插件" },
  { id: "logs", icon: "terminal", label: "日志" },
];

// 线性图标（stroke=1.8，Heroicons outline 风格）
const ICON_PATHS: Record<string, string[]> = {
  home: ["M2.25 12l8.954-8.955c.44-.439 1.152-.439 1.591 0L21.75 12M4.5 9.75v10.125c0 .621.504 1.125 1.125 1.125H9.75v-4.875c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125V21h4.125c.621 0 1.125-.504 1.125-1.125V9.75"],
  cube: [
    "M12 3.75l7.5 4.5v7.5l-7.5 4.5-7.5-4.5v-7.5l7.5-4.5z",
    "M4.5 8.25l7.5 4.5 7.5-4.5",
    "M12 12.75v7.5",
  ],
  user: [
    "M15.75 6a3.75 3.75 0 11-7.5 0 3.75 3.75 0 017.5 0z",
    "M4.501 20.118a7.5 7.5 0 0114.998 0A17.933 17.933 0 0112 21.75c-2.676 0-5.216-.584-7.499-1.632z",
  ],
  sliders: [
    "M10.5 6h9.75M10.5 6a1.5 1.5 0 11-3 0m3 0a1.5 1.5 0 10-3 0M3.75 6H7.5m3 12h9.75m-9.75 0a1.5 1.5 0 01-3 0m3 0a1.5 1.5 0 00-3 0m-3.75 0H7.5m9-6h3.75m-3.75 0a1.5 1.5 0 01-3 0m3 0a1.5 1.5 0 00-3 0m-9.75 0h9.75",
  ],
  grid: [
    "M3.75 6A2.25 2.25 0 016 3.75h2.25A2.25 2.25 0 0110.5 6v2.25a2.25 2.25 0 01-2.25 2.25H6a2.25 2.25 0 01-2.25-2.25V6z",
    "M13.5 6a2.25 2.25 0 012.25-2.25H18A2.25 2.25 0 0120.25 6v2.25A2.25 2.25 0 0118 10.5h-2.25a2.25 2.25 0 01-2.25-2.25V6z",
    "M3.75 15.75A2.25 2.25 0 016 13.5h2.25a2.25 2.25 0 012.25 2.25V18a2.25 2.25 0 01-2.25 2.25H6A2.25 2.25 0 013.75 18v-2.25z",
    "M13.5 15.75a2.25 2.25 0 012.25-2.25H18a2.25 2.25 0 012.25 2.25V18A2.25 2.25 0 0118 20.25h-2.25A2.25 2.25 0 0113.5 18v-2.25z",
  ],
  terminal: [
    "M6.75 7.5l3 2.25-3 2.25m4.5 0h3",
    "M3.75 21h16.5a.75.75 0 00.75-.75V3.75a.75.75 0 00-.75-.75H3.75a.75.75 0 00-.75.75v16.5c0 .414.336.75.75.75z",
  ],
  window: [
    "M3.75 3h16.5a.75.75 0 01.75.75v16.5a.75.75 0 01-.75.75H3.75a.75.75 0 01-.75-.75V3.75a.75.75 0 01.75-.75z",
    "M3 8.25h18",
    "M6 5.25h.01",
    "M9 5.25h.01",
  ],
};

function NavIcon({ name }: { name: string }) {
  const paths = ICON_PATHS[name] ?? ICON_PATHS.window;
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      {paths.map((d, i) => (
        <path key={i} d={d} />
      ))}
    </svg>
  );
}

// 无边框窗口的自绘标题栏（拖动区 + 窗口控制）
function TitleBar() {
  const [win, setWin] = useState<ReturnType<typeof getCurrentWindow> | null>(null);
  useEffect(() => {
    try {
      setWin(getCurrentWindow());
    } catch {
      /* 非 Tauri 环境（浏览器预览）时降级为空操作 */
    }
  }, []);
  return (
    <div class="titlebar" data-tauri-drag-region>
      <span class="titlebar-title">Starcloud Minecraft Launcher</span>
      <div class="titlebar-controls">
        <button onClick={() => win?.minimize()} aria-label="最小化">
          ─
        </button>
        <button onClick={() => win?.toggleMaximize()} aria-label="最大化">
          □
        </button>
        <button class="tb-close" onClick={() => win?.close()} aria-label="关闭">
          ×
        </button>
      </div>
    </div>
  );
}

export function App() {
  const [page, setPage] = useState<PageId>("home");
  const [status, setStatus] = useState<AppStatus>("idle");
  const [statusDetail, setStatusDetail] = useState<string | null>(null);
  const [progress, setProgress] = useState<ProgressPayload | null>(null);
  const [gameLogs, setGameLogs] = useState<string[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);
  const [guiPlugins, setGuiPlugins] = useState<GuiPluginInfo[]>([]);

  useEffect(() => {
    const unsubs: (() => void)[] = [];

    listen<StatusPayload>("launcher-status", (e) => {
      setStatus(e.payload.status as AppStatus);
      setStatusDetail(e.payload.detail);
    }).then((u) => unsubs.push(u));

    listen<ProgressPayload>("download-progress", (e) => setProgress(e.payload)).then((u) =>
      unsubs.push(u),
    );

    listen<InstallDonePayload>("install-done", (e) => {
      setStatus("idle");
      setProgress(null);
      if (e.payload.success) setStatusDetail(`${e.payload.version_id} 安装完成`);
      else setLastError(e.payload.message);
    }).then((u) => unsubs.push(u));

    listen<GameLogPayload>("game-log", (e) => {
      setGameLogs((prev) => [...prev.slice(-499), e.payload.line]);
    }).then((u) => unsubs.push(u));

    listen<GameExitedPayload>("game-exited", (e) => {
      setStatus("idle");
      setStatusDetail(e.payload.message);
      setProgress(null);
    }).then((u) => unsubs.push(u));

    listen<ErrorPayload>("error-occurred", (e) => {
      setStatus("error");
      setLastError(`${e.payload.context}: ${e.payload.message}`);
      setProgress(null);
    }).then((u) => unsubs.push(u));

    getGuiPlugins()
      .then(setGuiPlugins)
      .catch(() => {});

    return () => unsubs.forEach((u) => u());
  }, []);

  const viewPlugins = guiPlugins.filter((p) => p.gui_type === "view" && p.compatible);

  const renderPage = () => {
    switch (page) {
      case "home":
        return (
          <Home
            status={status}
            statusDetail={statusDetail}
            progress={progress}
            lastError={lastError}
            onClearError={() => setLastError(null)}
          />
        );
      case "versions":
        return <Versions />;
      case "accounts":
        return <Accounts />;
      case "settings":
        return <Settings />;
      case "plugins":
        return <Plugins />;
      case "logs":
        return <Logs liveLines={gameLogs} />;
      default:
        return <PluginView pluginId={page.slice(5)} />;
    }
  };

  return (
    <div class="shell">
      <TitleBar />
      <div class="shell-body">
        <aside class="sidebar">
        <div class="brand">
          <div class="brand-logo">
            <svg viewBox="0 0 32 32">
              <path d="M16 7l2.2 4.9 5.3.6-3.9 3.6 1 5.2L16 18.4l-4.6 2.9 1-5.2-3.9-3.6 5.3-.6z" />
            </svg>
          </div>
          <div>
            <div class="brand-name">Starcloud</div>
            <div class="brand-sub">Minecraft Launcher</div>
          </div>
        </div>

        {NAV.map((n) => (
          <button
            key={n.id}
            class={`nav-item ${page === n.id ? "active" : ""}`}
            onClick={() => setPage(n.id)}
          >
            <span class="nav-icon">
              <NavIcon name={n.icon} />
            </span>
            {n.label}
          </button>
        ))}

        {viewPlugins.length > 0 && <div class="nav-section">插件视图</div>}
        {viewPlugins.map((p) => (
          <button
            key={p.id}
            class={`nav-item ${page === `view:${p.id}` ? "active" : ""}`}
            onClick={() => setPage(`view:${p.id}` as PageId)}
          >
            <span class="nav-icon">
              <NavIcon name="window" />
            </span>
            {p.title || p.name}
          </button>
        ))}

        <div class="sidebar-footer">
          <span>Starcloud</span>
          <span class="ver">v0.2.0</span>
        </div>
      </aside>

      <main class="main">
        <div class="statusbar">
          <span class={`status-dot ${status}`} />
          <span class="status-text">{statusText(status, statusDetail)}</span>
        </div>
        <div class="content">{renderPage()}</div>
      </main>
      </div>
    </div>
  );
}

function statusText(status: AppStatus, detail: string | null): string {
  switch (status) {
    case "installing":
      return detail ? `安装中 · ${detail}` : "安装中…";
    case "launching":
      return detail ? `启动中 · ${detail}` : "启动中…";
    case "running":
      return "游戏运行中";
    case "error":
      return "发生错误";
    default:
      return detail ?? "就绪";
  }
}
