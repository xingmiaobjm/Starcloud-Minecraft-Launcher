// Starcloud Minecraft Launcher — Fluent Native（Microsoft Store 布局）
// 常驻展开 NavigationView · 品牌区在侧栏顶部 · 调度中心在侧栏底部（Store 下载队列式）

import { useEffect, useRef, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  AppWindow,
  Box,
  ClipboardList,
  Download as DownloadIcon,
  House,
  LayoutGrid,
  Minus,
  Puzzle,
  SlidersHorizontal,
  Square,
  SquareTerminal,
  UserRound,
  Wifi,
  X,
} from "lucide";
import {
  getAppVersion,
  getGuiPlugins,
  getLauncherLogs,
  getSettings,
  listTasks,
  searchMods,
  setTaskOrder,
} from "./api";
import { applyAnimScale, pageEnter } from "./motion";
import { initAccent, initTheme } from "./theme";
import { friendlyError } from "./errorMessages";
import { normalizeIcon } from "./pages/Mods";
import { Icon } from "./components/Icon/Icon";
import { SchedulerDrawer } from "./components/SchedulerDrawer";
import type {
  AppStatus,
  ErrorPayload,
  GameExitedPayload,
  GuiPluginInfo,
  InstallDonePayload,
  ProgressPayload,
  StatusPayload,
  TaskInfo,
  TasksUpdatedPayload,
} from "./types";
import { Home } from "./pages/Home";
import { Versions } from "./pages/Versions";
import { Download } from "./pages/Download";
import { Mods } from "./pages/Mods";
import { Terracotta } from "./pages/Terracotta";
import { Accounts } from "./pages/Accounts";
import { Settings } from "./pages/Settings";
import { Plugins } from "./pages/Plugins";
import { PluginView } from "./pages/PluginView";
import styles from "./app.module.css";

type PageId =
  | "home"
  | "versions"
  | "download"
  | "mods"
  | "terracotta"
  | "accounts"
  | "settings"
  | "plugins"
  | `view:${string}`;

const NAV: { id: PageId; icon: typeof House; label: string }[] = [
  { id: "home", icon: House, label: "首页" },
  { id: "versions", icon: Box, label: "版本" },
  { id: "download", icon: DownloadIcon, label: "下载" },
  { id: "mods", icon: Puzzle, label: "模组资源" },
  { id: "terracotta", icon: Wifi, label: "联机" },
  { id: "accounts", icon: UserRound, label: "账户" },
  { id: "settings", icon: SlidersHorizontal, label: "设置" },
  { id: "plugins", icon: LayoutGrid, label: "插件" },
];

function TitleBar() {
  const onMouseDown = (e: MouseEvent) => {
    const t = e.target as HTMLElement;
    if (t.closest("button, input, select, a, textarea")) return;
    if (e.button !== 0) return;
    try {
      getCurrentWindow().startDragging().catch(() => {});
    } catch {
      /* ignore */
    }
  };

  return (
    <div class={styles.titlebar} onMouseDown={onMouseDown}>
      <div class={styles.titlebarControls}>
        <button
          class={styles.titlebarBtn}
          onClick={() => getCurrentWindow().minimize()}
          aria-label="最小化"
        >
          <Icon icon={Minus} size={12} strokeWidth={1.2} />
        </button>
        <button
          class={styles.titlebarBtn}
          onClick={() => getCurrentWindow().toggleMaximize()}
          aria-label="最大化"
        >
          <Icon icon={Square} size={12} strokeWidth={1.2} />
        </button>
        <button
          class={`${styles.titlebarBtn} ${styles.titlebarClose}`}
          onClick={() => getCurrentWindow().close()}
          aria-label="关闭"
        >
          <Icon icon={X} size={12} strokeWidth={1.2} />
        </button>
      </div>
    </div>
  );
}

function FloatingLogButton({
  status,
  enabled,
}: {
  status: AppStatus;
  enabled: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [content, setContent] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (status !== "running") setOpen(false);
  }, [status]);

  if (status !== "running" || !enabled) return null;

  const toggle = async () => {
    const next = !open;
    setOpen(next);
    if (next && content === null) {
      setLoading(true);
      try {
        setContent(await getLauncherLogs());
      } catch {
        setContent("（读取启动器日志失败）");
      }
      setLoading(false);
    }
  };

  return (
    <div class={styles.floatLog}>
      {open && (
        <div class={styles.floatLogPanel}>
          <div class={styles.floatLogHead}>
            <span>启动器日志</span>
            <button class={styles.floatLogClose} onClick={() => setOpen(false)} aria-label="关闭日志">
              <Icon icon={X} size={14} strokeWidth={1.5} />
            </button>
          </div>
          <pre class={`${styles.floatLogBody} selectable`}>
            {loading ? "加载中…" : content ?? "（暂无日志）"}
          </pre>
        </div>
      )}
      <button class={styles.floatLogBtn} onClick={toggle} aria-label="启动器日志" title="启动器日志">
        <Icon icon={SquareTerminal} size={18} strokeWidth={1.5} />
      </button>
    </div>
  );
}

export function App() {
  const [page, setPage] = useState<PageId>("home");
  const [status, setStatus] = useState<AppStatus>("idle");
  const [statusDetail, setStatusDetail] = useState<string | null>(null);
  const [progress, setProgress] = useState<ProgressPayload | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [guiPlugins, setGuiPlugins] = useState<GuiPluginInfo[]>([]);
  const [tasks, setTasks] = useState<TaskInfo[]>([]);
  const [schedulerOpen, setSchedulerOpen] = useState(false);
  const [showLogBtn, setShowLogBtn] = useState(true);
  const [appVersion, setAppVersion] = useState("");
  const contentRef = useRef<HTMLElement>(null);

  const reorderTasks = (ids: string[]) => {
    setTasks((prev) => {
      const byId = new Map(prev.map((t) => [t.id, t]));
      const ordered = ids.map((id) => byId.get(id)).filter((t): t is TaskInfo => Boolean(t));
      const rest = prev.filter((t) => !ids.includes(t.id));
      return [...ordered, ...rest];
    });
    setTaskOrder(ids).catch(() => {});
  };

  useEffect(() => {
    const stop = (e: MouseEvent) => e.preventDefault();
    document.addEventListener("contextmenu", stop);
    return () => document.removeEventListener("contextmenu", stop);
  }, []);

  useEffect(() => {
    getAppVersion().then(setAppVersion).catch(() => {});
  }, []);

  useEffect(() => {
    const el = contentRef.current;
    if (!el) return;
    const mm = pageEnter(el);
    return () => mm.revert();
  }, [page]);

  useEffect(() => {
    const unsubs: (() => void)[] = [];
    if (!("__TAURI_INTERNALS__" in window)) return;

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
      else setLastError(friendlyError(e.payload.message));
    }).then((u) => unsubs.push(u));

    listen<GameExitedPayload>("game-exited", (e) => {
      setStatus("idle");
      setStatusDetail(e.payload.message);
      setProgress(null);
    }).then((u) => unsubs.push(u));

    listen<ErrorPayload>("error-occurred", (e) => {
      setStatus("error");
      setLastError(friendlyError(`${e.payload.context}: ${e.payload.message}`));
      setProgress(null);
    }).then((u) => unsubs.push(u));

    listen<TasksUpdatedPayload>("tasks-updated", (e) => setTasks(e.payload.tasks)).then((u) =>
      unsubs.push(u),
    );

    getGuiPlugins().then(setGuiPlugins).catch(() => {});
    listTasks().then(setTasks).catch(() => {});
    initTheme();
    initAccent();

    getSettings()
      .then((s) => {
        applyAnimScale(s.animation_scale);
        setShowLogBtn(s.show_log_button);
      })
      .catch(() => {});

    return () => unsubs.forEach((u) => u());
  }, []);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let cancelled = false;
    searchMods("", "", "", "mod", "", "modrinth", 0)
      .then((hits) => {
        if (cancelled) return;
        for (const h of hits) {
          const src = normalizeIcon(h.icon_url);
          if (!src) continue;
          const img = new Image();
          img.src = src;
        }
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, []);

  const viewPlugins = guiPlugins.filter(
    (p) => p.gui_type === "view" && p.compatible && p.enabled,
  );

  // 调度中心徽标：运行中任务数（Store 下载队列式入口）
  const runningTasks = tasks.filter((t) => t.status === "running").length;

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
            onGoDownload={() => setPage("download")}
            onGoAccounts={() => setPage("accounts")}
            onGoMods={() => setPage("mods")}
            onGoTerracotta={() => setPage("terracotta")}
          />
        );
      case "versions":
        return <Versions onGoDownload={() => setPage("download")} />;
      case "download":
        return <Download />;
      case "mods":
        return <Mods />;
      case "terracotta":
        return <Terracotta />;
      case "accounts":
        return <Accounts />;
      case "settings":
        return <Settings />;
      case "plugins":
        return <Plugins />;
      default:
        return <PluginView pluginId={page.slice(5)} />;
    }
  };

  return (
    <div class={styles.window}>
      <div class={styles.shell}>
        {/* 侧栏：常驻展开 NavigationView（Store 布局，占满全高） */}
        <aside class={styles.sidebar}>
          <div class={styles.brand} aria-hidden="true">
            <svg class={styles.brandIcon} viewBox="0 0 32 32">
              <path d="M16 6l3 6.5 7 1-5 4.5 1.5 6.5-6.5-3.5-6.5 3.5L11 18l-5-4.5 7-1z" />
            </svg>
            <span class={styles.brandName}>Starcloud</span>
          </div>

          <nav class={styles.nav} aria-label="主导航">
            {NAV.map((n) => (
              <button
                key={n.id}
                class={`${styles.navItem} ${page === n.id ? styles.navItemActive : ""}`}
                onClick={() => setPage(n.id)}
                aria-current={page === n.id ? "page" : undefined}
              >
                <span class={styles.navIcon}>
                  <Icon icon={n.icon} size={18} strokeWidth={1.5} />
                </span>
                <span class={styles.navLabel}>{n.label}</span>
              </button>
            ))}

            {viewPlugins.length > 0 && <div class={styles.navSection}>插件视图</div>}
            {viewPlugins.map((p) => (
              <button
                key={p.id}
                class={`${styles.navItem} ${page === `view:${p.id}` ? styles.navItemActive : ""}`}
                onClick={() => setPage(`view:${p.id}` as PageId)}
                aria-current={page === `view:${p.id}` ? "page" : undefined}
              >
                <span class={styles.navIcon}>
                  <Icon icon={AppWindow} size={18} strokeWidth={1.5} />
                </span>
                <span class={styles.navLabel}>{p.title || p.name}</span>
              </button>
            ))}
          </nav>

          <div class={styles.sidebarFooter}>
            <button
              class={`${styles.navItem} ${schedulerOpen ? styles.navItemActive : ""}`}
              onClick={() => setSchedulerOpen(true)}
              aria-label={`调度中心，${runningTasks} 个运行中任务`}
              title="调度中心"
            >
              <span class={styles.navIcon}>
                <Icon icon={ClipboardList} size={18} strokeWidth={1.5} />
              </span>
              <span class={styles.navLabel}>调度中心</span>
              {runningTasks > 0 && (
                <span class={styles.navBadge} aria-hidden="true">
                  {runningTasks}
                </span>
              )}
            </button>
            {appVersion && (
              <div class={styles.sidebarVersion}>{`v${appVersion}`}</div>
            )}
          </div>
        </aside>

        {/* 主列：标题栏（拖拽 + caption）+ 内容 */}
        <main class={styles.main}>
          <TitleBar />
          <section class={styles.content} ref={contentRef}>
            {renderPage()}
          </section>
        </main>
      </div>

      <SchedulerDrawer
        open={schedulerOpen}
        onClose={() => setSchedulerOpen(false)}
        tasks={tasks}
        onReorder={reorderTasks}
      />

      <FloatingLogButton status={status} enabled={showLogBtn} />
    </div>
  );
}
