// 设置：读取 / 保存启动器配置（Fluent Native：分组卡片 + 左标签右控件行式布局）
// 脏状态跟踪：表单快照对比，有未保存更改时顶部显示提示与保存按钮

import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import {
  checkJava,
  clearVersionCache,
  downloadJavaRuntime,
  getAppVersion,
  getDownloadSources,
  getLauncherLogs,
  getSettings,
  listJavaRuntimes,
  listTasks,
  pickDirectory,
  probeDownloadSources,
  removeJavaRuntime,
  setDownloadSource,
  setSettings,
  suggestMemory,
} from "../api";
import { applyAnimScale } from "../motion";
import { Check } from "lucide";
import { applyAccent, ACCENT_PRESETS, applyTheme, getAccentId, getTheme, type Theme } from "../theme";
import { Button } from "../components/Button/Button";
import { Card } from "../components/Card/Card";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Icon } from "../components/Icon/Icon";
import { Input } from "../components/Input/Input";
import { MaybeNotice, Notice } from "../components/Notice/Notice";
import { ProgressBar } from "../components/ProgressBar/ProgressBar";
import { Select } from "../components/Select/Select";
import { Slider } from "../components/Slider/Slider";
import { Spinner } from "../components/Spinner/Spinner";
import { Switch } from "../components/Switch/Switch";
import { Tag } from "../components/Tag/Tag";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { friendlyError } from "../errorMessages";
import { useNotice } from "../hooks/useNotice";
import { fmtBytes, fmtMemory, fmtSpeed } from "../utils/format";
import type {
  DownloadSourceId,
  DownloadSourceStatus,
  JavaInfo,
  JavaRuntime,
  MemorySuggestion,
  Settings as SettingsData,
  TaskInfo,
  TasksUpdatedPayload,
} from "../types";
import styles from "./Settings.module.css";

const EMPTY: SettingsData = {
  dot_minecraft: "",
  language: "zh_CN",
  default_min_memory_mb: 128,
  default_max_memory_mb: 4096,
  java_executable: null,
  extra_jvm_args: [],
  extra_game_args: [],
  use_high_performance_gpu: false,
  download_concurrency: 16,
  download_max_retries: 3,
  last_version: null,
  force_chinese: true,
  version_isolated: true,
  dev_mode: false,
  animation_scale: 1,
  show_log_button: true,
  version_cache_enabled: true,
  use_builtin_jvm_args: true,
};

/** 下载源 id → 显示名 */
const SOURCE_NAMES: Record<DownloadSourceId, string> = {
  official: "Mojang 官方",
  bmclapi: "BMCLAPI",
  fastmirror: "FastMinecraftMirror",
  rainyun: "RainYun 雨云",
  custom: "自定义镜像",
};

/** 可下载的 Java LTS 版本（Adoptium 提供） */
const JAVA_DOWNLOAD_OPTIONS = [8, 11, 17, 21, 25];

/** JVM 参数文本的规范化形式（连续空白合并），用于脏状态对比 */
const normArgs = (s: string) =>
  s
    .split(/\s+/)
    .filter(Boolean)
    .join(" ");

/** 已保存设置快照（脏状态对比基准） */
interface Snapshot {
  form: SettingsData;
  jvmArgs: string;
  gameArgs: string;
}

export function Settings() {
  const [form, setForm] = useState<SettingsData>(EMPTY);
  const [loaded, setLoaded] = useState(false);
  const [loadFailed, setLoadFailed] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [jvmArgs, setJvmArgs] = useState("");
  const [gameArgs, setGameArgs] = useState("");
  /** 已保存快照：form / 参数文本与当前值不一致即为脏 */
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [saving, setSaving] = useState(false);
  const [launcherLog, setLauncherLog] = useState<string | null>(null);

  const { notice, show, clear } = useNotice();

  const [java, setJava] = useState<JavaInfo | null | undefined>(undefined);
  /** 内存推荐值（进入页面取一次，可手动重新检测） */
  const [memAuto, setMemAuto] = useState<MemorySuggestion | null>(null);
  /** 内存控制模式：自动优化 / 手动配置 */
  const [memMode, setMemMode] = useState<"auto" | "manual">("auto");
  /** 外观主题（跟随系统 / 浅色 / 深色） */
  const [theme, setTheme] = useState<Theme>(getTheme());
  /** 主题色（system = 跟随系统强调色） */
  const [accent, setAccent] = useState(getAccentId());
  /** 下载源测速状态（进入页面拉取一次，后端推送 "download-sources" 事件实时刷新） */
  const [srcStatus, setSrcStatus] = useState<DownloadSourceStatus | null>(null);
  const [srcLoading, setSrcLoading] = useState(false);
  /** 可用 Java 运行时（启动器已下载 + 系统检测） */
  const [runtimes, setRuntimes] = useState<JavaRuntime[]>([]);
  /** 下载目标 Java 版本 */
  const [downloadVer, setDownloadVer] = useState<number>(21);
  /** 下载按钮点击后的短暂忙态（任务尚未出现在调度列表时防连点） */
  const [startBusy, setStartBusy] = useState(false);
  /** 进行中的 Java 下载任务（调度中心 tasks-updated 事件驱动） */
  const [javaTask, setJavaTask] = useState<TaskInfo | null>(null);
  const javaTaskIdRef = useRef<string | null>(null);
  /** 数字输入越界提示（并发线程数 / 最大重试） */
  const [concError, setConcError] = useState<string | undefined>(undefined);
  const [retryError, setRetryError] = useState<string | undefined>(undefined);
  /** 删除确认 */
  const [removeTarget, setRemoveTarget] = useState<JavaRuntime | null>(null);
  /** 启动器版本号（单一来源：后端 Cargo.toml，运行时获取） */
  const [appVersion, setAppVersion] = useState("");

  useEffect(() => {
    getAppVersion().then(setAppVersion).catch(() => {});
  }, []);

  // ── 设置加载：进入页面取一次；自动内存优化取一次推荐值（不再轮询）──
  const loadSettings = useCallback(async () => {
    setLoadFailed(false);
    setLoadError(null);
    try {
      const s = await getSettings();
      setForm(s);
      setJvmArgs(s.extra_jvm_args.join(" "));
      setGameArgs(s.extra_game_args.join(" "));
      setSnapshot({
        form: s,
        jvmArgs: normArgs(s.extra_jvm_args.join(" ")),
        gameArgs: normArgs(s.extra_game_args.join(" ")),
      });
      setLoaded(true);
      try {
        const sug = await suggestMemory();
        setMemAuto(sug);
        setForm((f) => ({
          ...f,
          default_min_memory_mb: sug.min_mb,
          default_max_memory_mb: sug.max_mb,
        }));
      } catch {
        /* 推荐值获取失败不阻塞页面，可点击「重新检测」重试 */
      }
    } catch (e) {
      setLoadFailed(true);
      setLoadError(friendlyError(e));
    }
  }, []);

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  // 下载源测速结果：进入页面拉取一次；启动测速完成后后端会推送 "download-sources" 事件实时刷新
  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    getDownloadSources()
      .then(setSrcStatus)
      .catch(() => {});
    const unsubs: (() => void)[] = [];
    listen<DownloadSourceStatus["results"]>("download-sources", (e) => {
      setSrcStatus((prev) => ({ active: prev?.active ?? "official", results: e.payload }));
    }).then((u) => unsubs.push(u));
    return () => unsubs.forEach((u) => u());
  }, []);

  // ── Java 下载任务跟踪 ──
  // 后端 download_java_runtime 为后台任务（kind = "java"），不广播 download-progress；
  // 阶段 / 状态经调度中心 "tasks-updated" 事件推送，此处据此渲染进度区。
  const loadJavaRuntimes = useCallback(async () => {
    try {
      setRuntimes(await listJavaRuntimes());
    } catch (e) {
      show({ text: `加载 Java 列表失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  }, [show]);

  const trackJavaTasks = useCallback(
    (tasks: TaskInfo[]) => {
      const running = tasks.find((t) => t.kind === "java" && t.status === "running") ?? null;
      setJavaTask(running);
      if (running) {
        javaTaskIdRef.current = running.id;
        return;
      }
      const finishedId = javaTaskIdRef.current;
      if (!finishedId) return;
      javaTaskIdRef.current = null;
      const finished = tasks.find((t) => t.id === finishedId);
      if (!finished) return;
      if (finished.status === "done") {
        show({ text: finished.detail || "Java 下载完成", kind: "ok" });
      } else if (finished.status === "error") {
        show({
          text: `下载 Java 失败：${friendlyError(finished.detail ?? "未知错误")}`,
          kind: "err",
          raw: finished.detail ?? undefined,
        });
      }
      loadJavaRuntimes();
    },
    [show, loadJavaRuntimes],
  );

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let cancelled = false;
    listTasks()
      .then((tasks) => {
        if (!cancelled) trackJavaTasks(tasks);
      })
      .catch(() => {});
    const un = listen<TasksUpdatedPayload>("tasks-updated", (e) => trackJavaTasks(e.payload.tasks));
    return () => {
      cancelled = true;
      un.then((u) => u());
    };
  }, [trackJavaTasks]);

  const set = <K extends keyof SettingsData>(key: K, value: SettingsData[K]) =>
    setForm((f) => ({ ...f, [key]: value }));

  // ── 脏状态：当前表单与已保存快照对比 ──
  const dirty =
    snapshot !== null &&
    (JSON.stringify(snapshot.form) !== JSON.stringify(form) ||
      snapshot.jvmArgs !== normArgs(jvmArgs) ||
      snapshot.gameArgs !== normArgs(gameArgs));

  /** 手动重新检测内存推荐值（自动优化模式下应用） */
  const refreshMemory = async (apply: boolean) => {
    try {
      const sug = await suggestMemory();
      setMemAuto(sug);
      if (apply) {
        setForm((f) => ({
          ...f,
          default_min_memory_mb: sug.min_mb,
          default_max_memory_mb: sug.max_mb,
        }));
      }
    } catch (e) {
      show({ text: `内存检测失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  };

  const handleTheme = (t: Theme) => {
    setTheme(t);
    applyTheme(t);
  };

  /** 数字输入失焦 clamp（并发 1-64 / 重试 0-10），越界时给出提示 */
  const applyClamp = (
    key: "download_concurrency" | "download_max_retries",
    min: number,
    max: number,
    onError: (msg: string | undefined) => void,
  ) => {
    const raw = Number(form[key]);
    if (Number.isFinite(raw) && raw >= min && raw <= max) return;
    const clamped = Math.min(max, Math.max(min, Number.isFinite(raw) ? Math.round(raw) : min));
    set(key, clamped);
    onError(`已调整为 ${clamped}（允许范围 ${min}-${max}）`);
  };

  /** 手动重新测速所有下载源并切换最快的可达源 */
  const refreshSources = async () => {
    setSrcLoading(true);
    try {
      setSrcStatus(await probeDownloadSources());
    } catch (e) {
      show({ text: `测速下载源失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    } finally {
      setSrcLoading(false);
    }
  };

  /** 手动切换下载源（覆盖自动测速的选择） */
  const selectSource = async (source: DownloadSourceId) => {
    try {
      await setDownloadSource(source);
      setSrcStatus((prev) =>
        prev ? { ...prev, active: source } : { active: source, results: [] },
      );
    } catch (e) {
      show({ text: `切换下载源失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  };

  const handleSave = async () => {
    setSaving(true);
    const args = jvmArgs.split(/\s+/).filter((s) => s.length > 0);
    const gargs = gameArgs.split(/\s+/).filter((s) => s.length > 0);
    try {
      const saved: SettingsData = { ...form, extra_jvm_args: args, extra_game_args: gargs };
      await setSettings(saved);
      // 保存后立即应用动画设置（无需重启）
      applyAnimScale(form.animation_scale);
      setSnapshot({
        form: saved,
        jvmArgs: normArgs(jvmArgs),
        gameArgs: normArgs(gameArgs),
      });
      show({ text: "设置已保存", kind: "ok" });
    } catch (e) {
      show({ text: `保存设置失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    } finally {
      setSaving(false);
    }
  };

  const handleCheckJava = async () => {
    setJava(undefined);
    try {
      const info = await checkJava();
      setJava(info);
      if (info) set("java_executable", info.executable);
    } catch (e) {
      setJava(null);
      show({ text: `Java 检测失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  };

  /** 全局 Java 选择框当前值：auto / 已装路径 / custom */
  const javaSelectValue = () => {
    const v = form.java_executable ?? "";
    if (!v) return "auto";
    return runtimes.some((r) => r.executable === v) ? v : "custom";
  };

  const handleDownloadJava = async () => {
    setStartBusy(true);
    try {
      await downloadJavaRuntime(downloadVer);
      // 已装同版本时后端直接返回；否则任务已创建，进度由 tasks-updated 事件驱动
      const tasks = await listTasks();
      trackJavaTasks(tasks);
      await loadJavaRuntimes();
    } catch (e) {
      show({ text: `下载 Java ${downloadVer} 失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    } finally {
      setStartBusy(false);
    }
  };

  const handleRemoveJava = async () => {
    const target = removeTarget;
    setRemoveTarget(null);
    if (!target) return;
    try {
      await removeJavaRuntime(target.major_version);
      // 若当前选中的正是被删的运行时，回退为自动选择
      if (form.java_executable === target.executable) set("java_executable", null);
      await loadJavaRuntimes();
      show({ text: `已删除 Java ${target.major_version}`, kind: "ok" });
    } catch (e) {
      show({
        text: `删除 Java ${target.major_version} 失败：${friendlyError(e)}`,
        kind: "err",
        raw: String(e),
      });
    }
  };

  // 进入设置页自动检测一次 Java（否则「检测 Java」按钮会因初始状态永久禁用）
  useEffect(() => {
    handleCheckJava();
    loadJavaRuntimes();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /** 打开系统原生「选择文件夹」对话框并回填游戏目录 */
  const handlePickDir = async () => {
    try {
      const dir = await pickDirectory();
      if (dir) set("dot_minecraft", dir);
    } catch (e) {
      show({ text: `选择目录失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  };

  /** 读取启动器本体日志（开发者模式） */
  const loadLauncherLogs = async () => {
    try {
      setLauncherLog(await getLauncherLogs());
    } catch (e) {
      show({ text: `读取启动器日志失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  };

  /** 清除版本清单本地缓存 */
  const handleClearCache = async () => {
    try {
      await clearVersionCache();
      show({ text: "缓存已清除", kind: "ok" });
    } catch (e) {
      show({ text: `清除版本缓存失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    }
  };

  const javaStatusText =
    java === undefined
      ? "正在检测系统 Java…"
      : java
        ? `${java.version_string}（Java ${java.major_version} · ${java.is_64bit ? "64 位" : "32 位"}）`
        : "未检测到系统 Java，可下载运行时或手动指定路径";

  if (!loaded) {
    return (
      <div class="page">
        <div class="page-header">
          <h1 class="page-title">设置</h1>
          <p class="page-desc">配置游戏目录、内存、Java 与下载参数</p>
        </div>
        {loadFailed ? (
          <EmptyState
            error
            title="设置加载失败"
            desc={loadError ?? "无法读取启动器配置"}
            action={
              <Button variant="standard" onClick={loadSettings}>
                重新加载
              </Button>
            }
          />
        ) : (
          <div class={styles.loading}>
            <Spinner size="lg" />
          </div>
        )}
      </div>
    );
  }

  return (
    <div class="page">
      <div class="page-header">
        <h1 class="page-title">设置</h1>
        <p class="page-desc">配置游戏目录、内存、Java 与下载参数</p>
      </div>

      <MaybeNotice notice={notice} onClose={clear} />

      {dirty && (
        <div class={styles.dirtyBar} role="status">
          <Notice notice={{ text: "有未保存的更改", kind: "info" }} className={styles.dirtyNotice} />
          <Button onClick={handleSave} busy={saving}>
            保存
          </Button>
        </div>
      )}

      {/* ---- 基础 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>基础</h2>
        <Card>
          <div class={styles.rowStack}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>游戏目录（.minecraft）</div>
              <div class={styles.rowDesc}>Minecraft 数据、存档与模组的存放位置</div>
            </div>
            <div class={styles.rowWide}>
              <Input
                className={styles.grow}
                value={form.dot_minecraft}
                placeholder="选择 Minecraft 游戏目录"
                readOnly
                title="点击打开目录选择"
                aria-label="游戏目录"
                onClick={handlePickDir}
              />
              <Button variant="standard" onClick={handlePickDir}>
                选择…
              </Button>
            </div>
          </div>

          <div class={styles.divider} />

          <div class={styles.rowStack}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>Java</div>
              <div class={styles.rowDesc}>{javaStatusText ?? "自动按版本要求匹配，或指定 Java 可执行文件路径"}</div>
            </div>
            <div class={styles.rowWide}>
              <Select
                className={styles.grow}
                ariaLabel="Java 运行时"
                value={javaSelectValue()}
                options={[
                  { value: "auto", label: "自动选择（按版本要求匹配已装/系统 Java）" },
                  ...runtimes.map((r) => ({
                    value: r.executable,
                    label: `Java ${r.major_version} · ${r.version_string}（${
                      r.is_64bit ? "64 位" : "32 位"
                    }${r.managed ? " · 启动器" : ""}）`,
                  })),
                  { value: "custom", label: "自定义路径…" },
                ]}
                onChange={(v) => {
                  if (v === "auto") set("java_executable", null);
                  else if (v !== "custom") set("java_executable", v);
                }}
              />
              <Button variant="standard" onClick={handleCheckJava}>
                检测 Java
              </Button>
            </div>
            {javaSelectValue() === "custom" && (
              <Input
                className={styles.customJavaInput}
                value={form.java_executable ?? ""}
                placeholder="Java 可执行文件完整路径（如 …\bin\java.exe）"
                aria-label="自定义 Java 路径"
                onInput={(e) =>
                  set("java_executable", (e.target as HTMLInputElement).value || null)
                }
              />
            )}
          </div>
        </Card>
      </section>

      {/* ---- Java 运行时 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>Java 运行时</h2>
        <Card>
          <div class={styles.rowStack}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>下载 Java 运行时</div>
              <div class={styles.rowDesc}>
                老版本游戏建议用 Java 8，新版本建议 17/21；下载完成后在上方下拉框中切换
              </div>
            </div>
            <div class={styles.rowWide}>
              <Select
                className={styles.compactSelect}
                ariaLabel="选择要下载的 Java 版本"
                value={String(downloadVer)}
                options={JAVA_DOWNLOAD_OPTIONS.map((v) => ({ value: String(v), label: `Java ${v}` }))}
                onChange={(v) => setDownloadVer(Number(v))}
              />
              <Button variant="standard" onClick={handleDownloadJava} busy={startBusy || javaTask !== null}>
                下载
              </Button>
              <Button variant="subtle" onClick={loadJavaRuntimes}>
                刷新
              </Button>
            </div>
            {javaTask && (
              <div class={styles.javaProgress} role="status" aria-live="polite">
                <div class={styles.javaProgressHead}>
                  <span>{javaTask.title}</span>
                  <span>{javaTask.stage ?? "准备中…"}</span>
                </div>
                {typeof javaTask.progress === "number" ? (
                  <ProgressBar
                    percentage={javaTask.progress}
                    speedBps={javaTask.speed ?? undefined}
                    etaSecs={javaTask.eta ?? undefined}
                    label={javaTask.title}
                  />
                ) : (
                  <div class={styles.indeterminate} aria-hidden="true">
                    <span />
                  </div>
                )}
              </div>
            )}
          </div>

          <div class={styles.divider} />

          {runtimes.length === 0 ? (
            <div class={styles.emptyHint}>未找到 Java 运行时（点击「检测 Java」或手动下载）</div>
          ) : (
            <ul class={styles.javaList}>
              {runtimes.map((r) => (
                <li key={r.executable} class={styles.javaItem}>
                  <div class={styles.javaInfo}>
                    <div class={styles.javaMain}>
                      <span class={styles.javaVer}>Java {r.major_version}</span>
                      <Tag variant={r.managed ? "accent" : "default"}>
                        {r.managed ? "启动器管理" : "系统"}
                      </Tag>
                      <Tag variant="default">{r.is_64bit ? "64 位" : "32 位"}</Tag>
                      {form.java_executable === r.executable && (
                        <Tag variant="accent">当前使用</Tag>
                      )}
                    </div>
                    <div class={styles.javaPath} title={r.executable}>
                      {r.version_string} · {r.executable}
                    </div>
                  </div>
                  {r.managed && (
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => setRemoveTarget(r)}
                      aria-label={`删除 Java ${r.major_version}`}
                    >
                      删除
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Card>
      </section>

      {/* ---- 内存 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>内存</h2>
        <Card>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>自动优化内存</div>
              <div class={styles.rowDesc}>按本机物理内存自动计算推荐分配值，保存后生效</div>
            </div>
            <Switch
              checked={memMode === "auto"}
              onChange={(v) => setMemMode(v ? "auto" : "manual")}
              aria-label="自动优化内存"
            />
          </div>

          {memMode === "auto" ? (
            <div class={styles.row}>
              <div class={styles.rowText}>
                <div class={styles.rowTitle}>推荐分配</div>
                <div class={styles.rowDesc}>
                  {memAuto
                    ? `本机物理内存 ${(memAuto.total_mb / 1024).toFixed(1)} GB，当前推荐 ${fmtMemory(
                        memAuto.min_mb,
                      )} / ${fmtMemory(memAuto.max_mb)}`
                    : "尚未获取推荐值，可点击「重新检测」"}
                </div>
              </div>
              <Button variant="standard" size="sm" onClick={() => refreshMemory(true)}>
                重新检测
              </Button>
            </div>
          ) : (
            <div class={styles.rowStack}>
              <div class={styles.rowText}>
                <div class={styles.rowTitle}>内存分配</div>
                <div class={styles.rowDesc}>
                  {form.dev_mode
                    ? "最小内存不可超过最大内存"
                    : "最小内存默认 128 MB，开启开发者模式后可调整"}
                </div>
              </div>
              <div class={styles.sliderRow}>
                <span class={styles.sliderLabel}>最大内存</span>
                <Slider
                  className={styles.sliderGrow}
                  min={0}
                  max={24576}
                  step={256}
                  value={form.default_max_memory_mb}
                  onChange={(mb) =>
                    set("default_max_memory_mb", Math.max(mb, form.default_min_memory_mb))
                  }
                  ariaLabel="最大内存"
                />
                <span class={styles.sliderValue}>{fmtMemory(form.default_max_memory_mb)}</span>
              </div>
              {form.dev_mode && (
                <div class={styles.sliderRow}>
                  <span class={styles.sliderLabel}>最小内存</span>
                  <Slider
                    className={styles.sliderGrow}
                    min={0}
                    max={24576}
                    step={256}
                    value={form.default_min_memory_mb}
                    onChange={(mb) =>
                      set("default_min_memory_mb", Math.min(mb, form.default_max_memory_mb))
                    }
                    ariaLabel="最小内存"
                  />
                  <span class={styles.sliderValue}>{fmtMemory(form.default_min_memory_mb)}</span>
                </div>
              )}
            </div>
          )}
        </Card>
      </section>

      {/* ---- 下载 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>下载</h2>
        <Card>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>并发线程数</div>
              <div class={styles.rowDesc}>越高下载越快，带宽充足时建议 16-32</div>
            </div>
            <Input
              className={styles.numInput}
              type="number"
              min={1}
              max={64}
              value={form.download_concurrency}
              error={concError}
              aria-label="并发线程数"
              onInput={(e) => {
                setConcError(undefined);
                set("download_concurrency", Number((e.target as HTMLInputElement).value));
              }}
              onBlur={() => applyClamp("download_concurrency", 1, 64, setConcError)}
            />
          </div>

          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>最大重试</div>
              <div class={styles.rowDesc}>下载失败后的自动重试次数</div>
            </div>
            <Input
              className={styles.numInput}
              type="number"
              min={0}
              max={10}
              value={form.download_max_retries}
              error={retryError}
              aria-label="最大重试"
              onInput={(e) => {
                setRetryError(undefined);
                set("download_max_retries", Number((e.target as HTMLInputElement).value));
              }}
              onBlur={() => applyClamp("download_max_retries", 0, 10, setRetryError)}
            />
          </div>

          <div class={styles.divider} />

          <div class={styles.rowStack}>
            <div class={styles.rowHead}>
              <div class={styles.rowText}>
                <div class={styles.rowTitle}>下载源</div>
                <div class={styles.rowDesc}>
                  启动时自动测速选优；手动选择立即生效，「重新测速」会切换为最快的可达源
                </div>
              </div>
              <Button variant="standard" size="sm" onClick={refreshSources} busy={srcLoading}>
                {srcLoading ? "测速中…" : "重新测速"}
              </Button>
            </div>
            <div class={styles.srcPicker} role="group" aria-label="选择下载源">
              {(Object.keys(SOURCE_NAMES) as DownloadSourceId[]).map((id) => (
                <button
                  type="button"
                  key={id}
                  class={`${styles.srcOpt}${srcStatus?.active === id ? ` ${styles.srcOptActive}` : ""}`}
                  aria-pressed={srcStatus?.active === id}
                  onClick={() => selectSource(id)}
                >
                  {SOURCE_NAMES[id]}
                </button>
              ))}
            </div>
            {srcStatus && srcStatus.results.length > 0 ? (
              <div class={styles.srcTable}>
                {srcStatus.results.map((r) => {
                  const isActive = srcStatus.active === r.source;
                  return (
                    <div
                      class={`${styles.srcRow}${isActive ? ` ${styles.srcRowActive}` : ""}`}
                      key={r.source}
                    >
                      <span class={styles.srcName}>
                        {SOURCE_NAMES[r.source] ?? r.source}
                        {isActive && <em class={styles.srcNow}>当前</em>}
                      </span>
                      <span class={r.reachable ? styles.srcOk : styles.srcBad}>
                        {r.reachable ? "可用" : "不可达"}
                      </span>
                      <span>{r.reachable ? `${r.latency_ms} ms` : "—"}</span>
                      <span class={styles.srcSpeed}>
                        {r.reachable ? fmtSpeed(r.speed_bps) : (r.error ?? "连接失败")}
                      </span>
                    </div>
                  );
                })}
              </div>
            ) : (
              <div class={styles.emptyHint}>正在测速下载源，稍候自动刷新…</div>
            )}
          </div>
        </Card>
      </section>

      {/* ---- 游戏 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>游戏</h2>
        <Card>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>游戏语言</div>
              <div class={styles.rowDesc}>传递给游戏的语言设置，下次启动游戏时生效</div>
            </div>
            <Select
              className={styles.langSelect}
              ariaLabel="游戏语言"
              value={form.language}
              options={[
                { value: "zh_CN", label: "简体中文" },
                { value: "en_US", label: "English" },
              ]}
              onChange={(lang) => {
                set("language", lang);
                set("force_chinese", lang.startsWith("zh"));
              }}
            />
          </div>

          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>版本隔离</div>
              <div class={styles.rowDesc}>
                每个版本使用独立游戏目录（versions/&lt;版本&gt;/game），默认开启；可在各版本设置中单独覆盖
              </div>
            </div>
            <Switch
              checked={form.version_isolated}
              onChange={(v) => set("version_isolated", v)}
              aria-label="版本隔离"
            />
          </div>

          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>内置 JVM 优化参数</div>
              <div class={styles.rowDesc}>
                自动优化游戏运行性能，减少卡顿和闪退（包括内存管理、网络优化等）；可在各版本设置中单独覆盖
              </div>
            </div>
            <Switch
              checked={form.use_builtin_jvm_args}
              onChange={(v) => set("use_builtin_jvm_args", v)}
              aria-label="内置 JVM 优化参数"
            />
          </div>
        </Card>
      </section>

      {/* ---- 外观 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>外观</h2>
        <Card>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>外观主题</div>
              <div class={styles.rowDesc}>跟随系统时自动切换明暗外观，立即生效</div>
            </div>
            <Select
              className={styles.langSelect}
              ariaLabel="外观主题"
              value={theme}
              options={[
                { value: "system", label: "跟随系统" },
                { value: "light", label: "浅色" },
                { value: "dark", label: "深色" },
              ]}
              onChange={(v) => handleTheme(v as Theme)}
            />
          </div>

          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>主题色</div>
              <div class={styles.rowDesc}>强调色会应用到启动按钮、导航高亮与进度条，立即生效</div>
            </div>
            <div class={styles.swatchRow} role="radiogroup" aria-label="主题色">
              {ACCENT_PRESETS.map((p) => (
                <button
                  key={p.id}
                  type="button"
                  role="radio"
                  aria-checked={accent === p.id}
                  aria-label={p.label}
                  title={p.label}
                  class={`${styles.swatch} ${accent === p.id ? styles.swatchActive : ""}`}
                  style={p.color ? { "--swatch-color": p.color, "--swatch-contrast": p.contrast } : undefined}
                  onClick={() => {
                    applyAccent(p.id);
                    setAccent(p.id);
                  }}
                >
                  {!p.color && <span class={styles.swatchSystem}>A</span>}
                  {accent === p.id && (
                    <Icon icon={Check} size={12} strokeWidth={3} class={styles.swatchCheck} />
                  )}
                </button>
              ))}
            </div>
          </div>

          <div class={styles.rowStack}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>动画倍率</div>
              <div class={styles.rowDesc}>数值越小动画越慢（0.1X 最慢，2X 最快），保存后生效</div>
            </div>
            <div class={styles.sliderRow}>
              <Slider
                className={styles.sliderGrow}
                min={0.1}
                max={2}
                step={0.1}
                value={form.animation_scale <= 0 ? 1 : form.animation_scale}
                disabled={form.animation_scale <= 0}
                onChange={(v) => set("animation_scale", v)}
                ariaLabel="动画倍率"
              />
              <span class={styles.sliderValue}>
                {form.animation_scale <= 0 ? "已关闭" : `${form.animation_scale.toFixed(1)}X`}
              </span>
            </div>
          </div>

          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>关闭动画</div>
              <div class={styles.rowDesc}>立即完成页面切换与交互过渡，适合低配设备</div>
            </div>
            <Switch
              checked={form.animation_scale <= 0}
              onChange={(v) => set("animation_scale", v ? 0 : 1)}
              aria-label="关闭动画"
            />
          </div>

          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>游戏运行时显示日志按钮</div>
              <div class={styles.rowDesc}>游戏启动后右下角出现小按钮，点击可查看启动器日志</div>
            </div>
            <Switch
              checked={form.show_log_button}
              onChange={(v) => set("show_log_button", v)}
              aria-label="游戏运行时显示日志按钮"
            />
          </div>
        </Card>
      </section>

      {/* ---- 版本缓存 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>版本缓存</h2>
        <Card>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>启用版本缓存</div>
              <div class={styles.rowDesc}>关闭后不缓存版本列表到本地，始终从网络拉取最新数据</div>
            </div>
            <Switch
              checked={form.version_cache_enabled}
              onChange={(v) => set("version_cache_enabled", v)}
              aria-label="启用版本缓存"
            />
          </div>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>清除版本缓存</div>
              <div class={styles.rowDesc}>重置本地缓存的版本清单数据，下次浏览版本页时将重新拉取全量清单</div>
            </div>
            <Button variant="standard" onClick={handleClearCache}>
              清除
            </Button>
          </div>
        </Card>
      </section>

      {/* ---- 开发者选项 ---- */}
      <section class={styles.group}>
        <h2 class={styles.groupTitle}>开发者选项</h2>
        <Card>
          <div class={styles.row}>
            <div class={styles.rowText}>
              <div class={styles.rowTitle}>开发者模式</div>
              <div class={styles.rowDesc}>开启后可查看启动器日志与高级启动参数</div>
            </div>
            <Switch
              checked={form.dev_mode}
              onChange={(v) => set("dev_mode", v)}
              aria-label="开发者模式"
            />
          </div>

          {form.dev_mode && (
            <>
              <div class={styles.divider} />

              <div class={styles.rowStack}>
                <div class={styles.rowText}>
                  <div class={styles.rowTitle}>启动器本体日志</div>
                  <div class={styles.rowDesc}>读取启动器运行日志的末尾内容，用于问题排查</div>
                </div>
                <div class={styles.rowWide}>
                  <Button variant="standard" onClick={loadLauncherLogs}>
                    {launcherLog === null ? "查看启动器日志" : "刷新日志"}
                  </Button>
                </div>
                {launcherLog !== null && (
                  <pre class={styles.devLog}>{launcherLog || "（暂无日志内容）"}</pre>
                )}
              </div>

              <div class={styles.row}>
                <div class={styles.rowText}>
                  <div class={styles.rowTitle}>Java 优先使用独立显卡</div>
                  <div class={styles.rowDesc}>
                    Windows 下为 Java 进程设置「独立显卡优先」，需重启游戏生效
                  </div>
                </div>
                <Switch
                  checked={form.use_high_performance_gpu}
                  onChange={(v) => set("use_high_performance_gpu", v)}
                  aria-label="Java 优先使用独立显卡"
                />
              </div>

              <div class={styles.rowStack}>
                <div class={styles.rowText}>
                  <div class={styles.rowTitle}>游戏优化启动参数（空格分隔）</div>
                  <div class={styles.rowDesc}>追加到游戏主类参数之后</div>
                </div>
                <Input
                  value={gameArgs}
                  placeholder="例如 -Dfml.ignoreInvalidMinecraftCertificates=true"
                  aria-label="游戏优化启动参数"
                  onInput={(e) => setGameArgs((e.target as HTMLInputElement).value)}
                />
              </div>

              <div class={styles.rowStack}>
                <div class={styles.rowText}>
                  <div class={styles.rowTitle}>Java 高级启动参数（空格分隔）</div>
                  <div class={styles.rowDesc}>内存参数已由内存配置自动生成，无需重复填写</div>
                </div>
                <Input
                  value={jvmArgs}
                  placeholder="例如 -XX:+UseG1GC"
                  aria-label="Java 高级启动参数"
                  onInput={(e) => setJvmArgs((e.target as HTMLInputElement).value)}
                />
              </div>
            </>
          )}
        </Card>
      </section>

      <div class={styles.footerVersion}>Starcloud v{appVersion}</div>

      <ConfirmDialog
        open={removeTarget !== null}
        title={`删除 Java ${removeTarget?.major_version ?? ""}`}
        message={`将删除 ${removeTarget?.executable ?? ""}，该操作不可恢复。`}
        confirmLabel="删除"
        danger
        onConfirm={handleRemoveJava}
        onCancel={() => setRemoveTarget(null)}
      />
    </div>
  );
}
