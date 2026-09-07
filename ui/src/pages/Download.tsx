// 下载页：浏览 Mojang 官方版本并安装（Fluent Native）
// 功能：类型筛选 + 搜索 + 大版本分组手风琴 + 行内进度（百分比/速度/ETA）+ 取消
// 「安装」弹出加载器选择：原版 / Fabric / Forge / Quilt / NeoForge（来自后端注册表），
// 选择后安装为独立版本（如 1.20.1-forge-47.2.0），加载器固定不可再更改；
// 已安装版本按钮变为「重新安装」，需确认覆盖后进入同一安装流程。
// 清单加载失败与空结果分离；工具栏可手动刷新清单；fromCache 时显示「本地缓存」。

import { useEffect, useRef, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import { ChevronDown, ChevronUp } from "lucide";
import { friendlyError } from "../errorMessages";
import grassIcon from "../assets/grass-block.png";
import cobblestoneIcon from "../assets/cobblestone.png";
import commandBlockIcon from "../assets/mc-icons/command_block.png";
import fabricIcon from "../assets/fabric.png";
import forgeIcon from "../assets/forge.png";
import quiltIcon from "../assets/quilt.png";
import neoforgeIcon from "../assets/neoforge.png";
import {
  cancelInstall,
  clearVersionCache,
  downloadServer,
  installLoader,
  listInstalled,
  listLoaders,
  listVersions,
} from "../api";
import type {
  InstallDonePayload,
  LoaderInfo,
  ProgressPayload,
  VersionInfo,
} from "../types";
import { Button } from "../components/Button/Button";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Icon } from "../components/Icon/Icon";
import { Input } from "../components/Input/Input";
import { Modal } from "../components/Modal/Modal";
import { MaybeNotice } from "../components/Notice/Notice";
import { ProgressBar } from "../components/ProgressBar/ProgressBar";
import { Spinner } from "../components/Spinner/Spinner";
import { Tag } from "../components/Tag/Tag";
import { useNotice } from "../hooks/useNotice";
import { groupToggle } from "../motion";
import { fmtBytes } from "../utils/format";
import styles from "./Download.module.css";

type FilterKey = "release" | "snapshot" | "old_beta" | "old_alpha" | "all";

const FILTERS: { key: FilterKey; label: string }[] = [
  { key: "release", label: "正式版" },
  { key: "snapshot", label: "快照版" },
  { key: "old_beta", label: "旧版 Beta" },
  { key: "old_alpha", label: "旧版 Alpha" },
  { key: "all", label: "全部" },
];

const TYPE_LABEL: Record<string, string> = {
  release: "正式版",
  snapshot: "快照版",
  old_beta: "旧版 Beta",
  old_alpha: "旧版 Alpha",
};

/** 加载器 ID → 官方像素 logo（vanilla 用草方块代表原版） */
const LOADER_ICONS: Record<string, string> = {
  vanilla: grassIcon,
  fabric: fabricIcon,
  forge: forgeIcon,
  quilt: quiltIcon,
  neoforge: neoforgeIcon,
};

/** 后端 version_type（Debug 输出）→ 筛选键 */
function typeKey(t: string): FilterKey | "other" {
  switch (t.toLowerCase()) {
    case "release":
      return "release";
    case "snapshot":
      return "snapshot";
    case "oldbeta":
      return "old_beta";
    case "oldalpha":
      return "old_alpha";
    default:
      return "other";
  }
}

/** 版本行图标：Beta/Alpha 用原石，快照版用命令方块，其余（正式版等）用草方块 */
function versionIcon(v: VersionInfo): string {
  const t = typeKey(v.version_type);
  if (t === "old_beta" || t === "old_alpha") return cobblestoneIcon;
  if (t === "snapshot") return commandBlockIcon;
  return grassIcon;
}

/** 组头图标：含 Beta/Alpha 的组用原石；纯快照组用命令方块；其余用草方块 */
function groupIcon(g: { items: VersionInfo[] }): string {
  const types = new Set(g.items.map((v) => typeKey(v.version_type)));
  if (types.has("old_beta") || types.has("old_alpha")) return cobblestoneIcon;
  if (types.size === 1 && types.has("snapshot")) return commandBlockIcon;
  return grassIcon;
}

/** 大版本分组键：正式版按 X.Y；pre/rc 快照归入对应大版本；周快照按年份；旧版按类型 */
function groupKey(v: VersionInfo): string {
  const t = typeKey(v.version_type);
  if (t === "release") {
    const parts = v.id.split(".");
    return parts.length >= 2 ? `${parts[0]}.${parts[1]}` : v.id;
  }
  if (t === "snapshot") {
    const pre = v.id.match(/^(\d+\.\d+)/);
    if (pre) return pre[1]; // 1.21-pre1 / 1.21.4-pre1 → 1.21
    const wk = v.id.match(/^(\d{2})w/);
    if (wk) return `snap-${wk[1]}`; // 24w21b → snap-24
    return "snap-other";
  }
  if (t === "old_beta") return "old-beta";
  if (t === "old_alpha") return "old-alpha";
  return "other";
}

/** 大版本分组显示名 */
function groupLabel(key: string): string {
  if (key === "old-beta") return "旧版 Beta";
  if (key === "old-alpha") return "旧版 Alpha";
  if (key === "other") return "其他";
  if (key === "snap-other") return "快照 · 其他";
  if (key.startsWith("snap-")) return `${key.slice(5)}W`; // snap-18 → 18W（周快照按年份）
  return key; // 大版本名直接显示，如 "1.21"
}

/** 新建任务占位进度（invoke 返回后立即显示"准备中"+取消按钮） */
function freshJob(versionId: string): ProgressPayload {
  return {
    version_id: versionId,
    downloaded_bytes: 0,
    total_bytes: 0,
    percentage: 0,
    stage: "准备中…",
    status: "Pending",
    speed_bytes_per_sec: 0,
    eta_secs: null,
  };
}

export function Download() {
  const [versions, setVersions] = useState<VersionInfo[]>([]);
  const [filter, setFilter] = useState<FilterKey>("release");
  const [search, setSearch] = useState("");
  const [loading, setLoading] = useState(true);
  /** 清单加载失败（区别于空列表：失败给重新加载入口） */
  const [loadError, setLoadError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [fromCache, setFromCache] = useState(false);
  const [loaders, setLoaders] = useState<LoaderInfo[]>([]);
  const [loadersLoading, setLoadersLoading] = useState(false);
  /** 加载器列表获取失败（弹窗内给重试按钮） */
  const [loadersError, setLoadersError] = useState<string | null>(null);
  /** 正在选择加载器的 MC 版本（弹窗打开时非 null） */
  const [picker, setPicker] = useState<string | null>(null);
  /** 待确认覆盖重装的版本 ID（ConfirmDialog 打开时非 null） */
  const [reinstallTarget, setReinstallTarget] = useState<string | null>(null);
  const [jobs, setJobs] = useState<Record<string, ProgressPayload>>({});
  const jobsRef = useRef<Record<string, ProgressPayload>>({});
  const { notice, show, clear } = useNotice();
  /** 大版本分组折叠状态（默认仅最新一组展开，点组头折叠） */
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  /** 分组主体 DOM 引用（供 GSAP 展开/收起动画） */
  const bodyRefs = useRef<Record<string, HTMLDivElement | null>>({});

  const updateJobs = (
    fn: (prev: Record<string, ProgressPayload>) => Record<string, ProgressPayload>,
  ) => {
    setJobs((prev) => {
      const next = fn(prev);
      jobsRef.current = next;
      return next;
    });
  };

  /** 标记已安装状态：加载器版本（如 1.20.1-forge-47.2.0 / fabric-loader-x-1.21.4）视为其对应 MC 版本已安装 */
  const refreshInstalled = () => {
    listInstalled()
      .then((inst) => {
        const ids = inst.map((i) => i.id);
        setVersions((prev) =>
          prev.map((v) => ({
            ...v,
            installed: ids.some(
              (id) => id === v.id || id.startsWith(`${v.id}-`) || id.endsWith(`-${v.id}`),
            ),
          })),
        );
      })
      .catch(() => {});
  };

  /** 拉取版本清单（后端缓存优先返回，远程刷新完成后走 versions-updated 事件覆盖） */
  const loadVersions = async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const list = await listVersions();
      setVersions(list);
      setFromCache(true);
      refreshInstalled();
    } catch (e) {
      setLoadError(friendlyError(e));
    } finally {
      setLoading(false);
    }
  };

  /** 手动刷新清单：清除本地缓存后重新拉取远程清单 */
  const refreshVersions = async () => {
    setRefreshing(true);
    try {
      await clearVersionCache();
      const list = await listVersions();
      setVersions(list);
      setFromCache(false);
      setLoadError(null);
      refreshInstalled();
      show({ text: "版本清单已更新", kind: "ok" });
    } catch (e) {
      show({ text: `刷新清单失败：${friendlyError(e)}`, kind: "err", raw: String(e) }, null);
    } finally {
      setRefreshing(false);
    }
  };

  /** 获取加载器注册表（每次打开弹窗重新拉取，失败可重试） */
  const fetchLoaders = async () => {
    setLoadersLoading(true);
    setLoadersError(null);
    try {
      setLoaders(await listLoaders());
    } catch (e) {
      setLoadersError(friendlyError(e));
    } finally {
      setLoadersLoading(false);
    }
  };

  useEffect(() => {
    void loadVersions();
    void fetchLoaders();
  }, []);

  // 监听后台刷新事件：远程数据拉取完成后覆盖列表并重新标记已装状态
  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let unsub: (() => void) | undefined;
    listen<VersionInfo[]>("versions-updated", (e) => {
      setVersions(e.payload);
      setFromCache(false);
      refreshInstalled();
    }).then((u) => {
      unsub = u;
    });
    return () => unsub?.();
  }, []);

  // 订阅进度与完成事件（仅更新本页发起的任务）
  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const unsubs: (() => void)[] = [];

    listen<ProgressPayload>("download-progress", (e) => {
      const p = e.payload;
      updateJobs((prev) => (p.version_id in prev ? { ...prev, [p.version_id]: p } : prev));
    }).then((u) => unsubs.push(u));

    listen<InstallDonePayload>("install-done", (e) => {
      const p = e.payload;
      const tracked = p.job_id in jobsRef.current;
      updateJobs((prev) => {
        if (!(p.job_id in prev)) return prev;
        const next = { ...prev };
        delete next[p.job_id];
        return next;
      });
      if (tracked) {
        if (p.success) {
          const isServer = p.job_id.startsWith("server:");
          show({
            text: isServer
              ? `${p.job_id.slice("server:".length)} 服务端下载完成`
              : `${p.version_id} 安装完成`,
            kind: "ok",
          });
          if (!isServer) refreshInstalled();
        } else {
          show({ text: p.message || "安装失败", kind: "err" }, null);
        }
      }
    }).then((u) => unsubs.push(u));

    return () => unsubs.forEach((u) => u());
  }, []);

  /** 打开加载器选择弹窗（每次打开重新拉取加载器列表） */
  const openPicker = (id: string) => {
    clear();
    setPicker(id);
    void fetchLoaders();
  };

  /** 行内「安装 / 重新安装」入口：已安装版本需先确认覆盖 */
  const requestInstall = (v: VersionInfo) => {
    clear();
    if (v.installed) setReinstallTarget(v.id);
    else openPicker(v.id);
  };

  /** 任务启动失败时移除占位进度（任务未建立，不会有进度/完成事件来清理） */
  const dropJob = (key: string) => {
    updateJobs((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
  };

  /** 选择加载器后开始安装（任务键 = {loader}:{mc}，安装后加载器固定不可更改） */
  const handlePickLoader = async (mc: string, loaderId: string) => {
    const key = `${loaderId}:${mc}`;
    setPicker(null);
    clear();
    updateJobs((prev) => ({ ...prev, [key]: freshJob(key) }));
    try {
      await installLoader(mc, loaderId);
    } catch (e) {
      dropJob(key);
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    }
  };

  const handleServer = async (id: string) => {
    const key = `server:${id}`;
    clear();
    updateJobs((prev) => ({ ...prev, [key]: freshJob(key) }));
    try {
      await downloadServer(id);
    } catch (e) {
      dropJob(key);
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    }
  };

  const handleCancel = async (key: string) => {
    try {
      await cancelInstall(key);
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    }
  };

  const shown = versions.filter((v) => {
    if (filter !== "all" && typeKey(v.version_type) !== filter) return false;
    if (search && !v.id.toLowerCase().includes(search.toLowerCase())) return false;
    return true;
  });
  const installedCount = versions.filter((v) => v.installed).length;
  const downloadingCount = Object.keys(jobs).length;

  /** 按大版本聚合（保持清单原有顺序，最新的组在前） */
  const groups: { key: string; label: string; items: VersionInfo[] }[] = [];
  const groupIndex = new Map<string, number>();
  for (const v of shown) {
    const key = groupKey(v);
    const idx = groupIndex.get(key);
    if (idx === undefined) {
      groupIndex.set(key, groups.length);
      groups.push({ key, label: groupLabel(key), items: [v] });
    } else {
      groups[idx].items.push(v);
    }
  }
  /** 搜索时强制全部展开，保证命中结果可见；默认仅最新（第一组）展开、其余折叠，点组头切换 */
  const searching = search.trim().length > 0;
  const isOpen = (key: string, index: number) =>
    searching || (collapsed[key] === undefined ? index === 0 : !collapsed[key]);
  const toggleGroup = (key: string, index: number) => {
    const next = !isOpen(key, index);
    if (searching) return; // 搜索中不折叠
    // 状态立即翻转（组头箭头即时回位），主体可见性由 GSAP 动画接管
    setCollapsed((prev) => ({ ...prev, [key]: !next }));
    const body = bodyRefs.current[key];
    if (body) groupToggle(body, next, "[data-dl-row]");
  };

  /** 单个版本行（分组展开后渲染） */
  const renderRow = (v: VersionInfo) => {
    // 本行安装任务键 = {loader}:{mc}（服务端任务键 = server:{mc}）
    const installJobKey = Object.keys(jobs).find(
      (k) => !k.startsWith("server:") && k.endsWith(`:${v.id}`),
    );
    const job = installJobKey ? jobs[installJobKey] : undefined;
    const serverJob = jobs[`server:${v.id}`];
    const downloading = Boolean(job);
    const downloadingServer = Boolean(serverJob);
    return (
      <div class={styles.row} data-dl-row key={v.id}>
        <div class={styles.rowTop}>
          {/* 图标按版本类型选用不同方块，与「版本ID + 类型/日期/大小」同行 */}
          <img class={styles.rowIcon} src={versionIcon(v)} alt="" draggable={false} />
          <div class={styles.rowMain}>
            <div class={styles.rowTitle}>
              <span class={styles.versionId}>{v.id}</span>
              <Tag variant="default">{TYPE_LABEL[typeKey(v.version_type)] ?? "其他"}</Tag>
              {v.installed && <Tag variant="success">已装</Tag>}
            </div>
            <div class={styles.rowMeta}>
              <span>{v.release_time || "发布时间未知"}</span>
              <span aria-hidden="true">·</span>
              <span>{fmtBytes(v.size_bytes)}</span>
            </div>
          </div>
          <div class={styles.rowActions}>
            {downloading && installJobKey && (
              <Button
                variant="danger"
                size="sm"
                onClick={() => handleCancel(installJobKey)}
                title="取消版本安装"
              >
                取消安装
              </Button>
            )}
            {downloadingServer && (
              <Button
                variant="danger"
                size="sm"
                onClick={() => handleCancel(`server:${v.id}`)}
                title="取消服务端下载"
              >
                取消服务端
              </Button>
            )}
            {!downloading && !downloadingServer && (
              <>
                <Button
                  variant="primary"
                  size="sm"
                  title={
                    v.installed
                      ? "重新安装将覆盖现有文件"
                      : "选择 Mod 加载器后安装（加载器固定，不可后期添加或更改）"
                  }
                  onClick={() => requestInstall(v)}
                >
                  {v.installed ? "重新安装" : "安装"}
                </Button>
                <Button
                  variant="standard"
                  size="sm"
                  title="下载该版本的官方服务端 jar"
                  onClick={() => handleServer(v.id)}
                >
                  服务端
                </Button>
              </>
            )}
          </div>
        </div>

        {downloading && job && (
          <ProgressBar
            className={styles.rowProgress}
            percentage={job.percentage}
            stage={job.stage || "下载中…"}
            speedBps={job.speed_bytes_per_sec}
            downloadedBytes={job.downloaded_bytes}
            totalBytes={job.total_bytes}
            etaSecs={job.eta_secs}
            label={`下载 ${v.id}`}
          />
        )}

        {downloadingServer && serverJob && (
          <ProgressBar
            className={styles.rowProgress}
            percentage={serverJob.percentage}
            stage={serverJob.stage || "服务端下载中…"}
            speedBps={serverJob.speed_bytes_per_sec}
            downloadedBytes={serverJob.downloaded_bytes}
            totalBytes={serverJob.total_bytes}
            etaSecs={serverJob.eta_secs}
            label={`下载 ${v.id} 服务端`}
          />
        )}
      </div>
    );
  };

  return (
    <div class={`page ${styles.page}`}>
      <header class="page-header">
        <h1 class="page-title">下载</h1>
        <p class="page-desc">
          浏览并安装 Minecraft 官方版本，可下载服务端或搭配 Mod 加载器，进度实时显示。
        </p>
      </header>

      <MaybeNotice notice={notice} onClose={clear} />

      <div class={`page-toolbar ${styles.toolbar}`}>
        <div class={styles.seg} role="tablist" aria-label="版本类型筛选">
          {FILTERS.map((f) => (
            <button
              key={f.key}
              type="button"
              role="tab"
              aria-selected={filter === f.key}
              class={`${styles.segItem} ${filter === f.key ? styles.segItemActive : ""}`}
              onClick={() => setFilter(f.key)}
            >
              {f.label}
            </button>
          ))}
        </div>
        <Input
          className={styles.searchInput}
          placeholder="搜索版本号…"
          aria-label="搜索版本号"
          value={search}
          onInput={(e) => setSearch((e.target as HTMLInputElement).value)}
        />
        <div class={styles.toolbarEnd}>
          {fromCache && (
            <Tag variant="warning" className={styles.cacheTag}>
              本地缓存
            </Tag>
          )}
          <Button
            variant="standard"
            busy={refreshing}
            onClick={() => void refreshVersions()}
            title="清除本地缓存并重新获取版本清单"
          >
            刷新清单
          </Button>
        </div>
      </div>

      <div class={styles.summary}>
        共 {versions.length} 个版本 · 当前显示 {shown.length} 个
        {installedCount > 0 && ` · 已安装 ${installedCount} 个`}
        {downloadingCount > 0 && ` · 下载中 ${downloadingCount} 个`}
      </div>

      <div class={styles.list}>
        {loading && (
          <EmptyState
            className={styles.listStatus}
            icon={<Spinner />}
            title="正在加载版本清单"
            desc="正在获取 Minecraft 官方版本列表，首次使用需要联网"
          />
        )}
        {!loading && loadError && (
          <EmptyState
            className={styles.listStatus}
            error
            title="版本清单加载失败"
            desc={loadError}
            action={<Button onClick={() => void loadVersions()}>重新加载</Button>}
          />
        )}
        {!loading && !loadError && shown.length === 0 && (
          <>
            {versions.length === 0 ? (
              <EmptyState
                className={styles.listStatus}
                title="暂无可用版本"
                desc="版本清单为空，可点击「刷新清单」重新获取"
              />
            ) : search.trim() ? (
              <EmptyState
                className={styles.listStatus}
                title={`未找到匹配「${search.trim()}」的版本`}
                desc="试试更换搜索关键词，或切换上方的版本类型筛选"
              />
            ) : (
              <EmptyState
                className={styles.listStatus}
                title="当前筛选条件下没有版本"
                desc="该类型下暂无版本，可切换到「全部」查看"
              />
            )}
          </>
        )}
        {!loading &&
          !loadError &&
          groups.map((g, gi) => (
            <div class={styles.group} key={g.key}>
              <button
                type="button"
                class={styles.groupHead}
                onClick={() => toggleGroup(g.key, gi)}
                aria-expanded={isOpen(g.key, gi)}
              >
                <img class={styles.groupIcon} src={groupIcon(g)} alt="" draggable={false} />
                <span class={styles.groupName}>{g.label}</span>
                <span class={styles.groupCount}>{g.items.length} 个版本</span>
                <Icon
                  icon={isOpen(g.key, gi) ? ChevronUp : ChevronDown}
                  size={16}
                  strokeWidth={1.8}
                  class={styles.groupCaret}
                />
              </button>
              <div
                class={`${styles.groupBody} ${isOpen(g.key, gi) ? styles.groupBodyOpen : ""}`}
                ref={(el) => {
                  bodyRefs.current[g.key] = el;
                }}
              >
                <div class={styles.groupBodyInner}>{g.items.map((v) => renderRow(v))}</div>
              </div>
            </div>
          ))}
      </div>

      {/* 加载器选择弹窗：选定后该版本加载器固定，不可后期添加或更改 */}
      <Modal
        open={picker !== null}
        onClose={() => setPicker(null)}
        title="选择 Mod 加载器"
        width="480px"
      >
        <p class={styles.modalSub}>
          为 {picker} 选择加载器。安装后生成独立版本（如 1.20.1-forge-47.2.0），加载器类型不可更改。
        </p>
        <div class={styles.loaderList}>
          {loadersLoading && (
            <div class={styles.loaderStatus}>
              <Spinner size="sm" />
              <span>正在获取加载器列表…</span>
            </div>
          )}
          {!loadersLoading && loadersError && (
            <EmptyState
              error
              title="加载器列表获取失败"
              desc={loadersError}
              action={<Button onClick={() => void fetchLoaders()}>重试</Button>}
            />
          )}
          {!loadersLoading &&
            !loadersError &&
            loaders.map((l) => (
              <button
                key={l.id}
                type="button"
                class={styles.loaderItem}
                onClick={() => handlePickLoader(picker!, l.id)}
              >
                <img
                  class={styles.loaderIcon}
                  src={LOADER_ICONS[l.id] ?? grassIcon}
                  alt=""
                  draggable={false}
                />
                <span class={styles.loaderText}>
                  <span class={styles.loaderName}>{l.label}</span>
                  <span class={styles.loaderDesc}>{l.description}</span>
                </span>
              </button>
            ))}
        </div>
        <div class={styles.modalActions}>
          <Button variant="standard" onClick={() => setPicker(null)}>
            取消
          </Button>
        </div>
      </Modal>

      {/* 重新安装确认：覆盖现有文件前需明确确认 */}
      <ConfirmDialog
        open={reinstallTarget !== null}
        title="重新安装"
        message={reinstallTarget ? `${reinstallTarget} 已安装，重新安装将覆盖现有文件。` : ""}
        confirmLabel="重新安装"
        onConfirm={() => {
          const id = reinstallTarget;
          setReinstallTarget(null);
          if (id) openPicker(id);
        }}
        onCancel={() => setReinstallTarget(null)}
      />
    </div>
  );
}
