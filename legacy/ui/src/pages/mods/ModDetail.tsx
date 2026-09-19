// 模组详情面板：宽卡片（图标/标题/描述 + MC 百科与平台官网链接 + 版本树下载）。
// 版本数据在挂载时拉取（忽略工具栏筛选，展示该项目全部版本，按大版本/加载器分组）；
// 下载与取消由容器接管（任务与进度事件在容器层，返回列表后进度仍持续显示）。

import { useEffect, useMemo, useState } from "preact/hooks";
import { ChevronDown, ChevronLeft, ChevronUp } from "lucide";
import { listModVersions } from "../../api";
import { friendlyError } from "../../errorMessages";
import { Button } from "../../components/Button/Button";
import { EmptyState } from "../../components/EmptyState/EmptyState";
import { Icon } from "../../components/Icon/Icon";
import { ProgressBar } from "../../components/ProgressBar/ProgressBar";
import { Spinner } from "../../components/Spinner/Spinner";
import { Tag } from "../../components/Tag/Tag";
import type { ModFile, ModSearchHit, ProgressPayload } from "../../types";
import { fmtBytes } from "../../utils/format";
import { ModIcon } from "./ModIcon";
import { fmtNum, placeholderLetter } from "./shared";
import styles from "./ModDetail.module.css";

/** 文件行元信息中预览的游戏版本个数（超出部分省略） */
const FILE_GV_PREVIEW = 3;

/** 加载器 id → 显示名 */
const LOADER_LABEL: Record<string, string> = {
  fabric: "Fabric",
  forge: "Forge",
  quilt: "Quilt",
  neoforge: "NeoForge",
  vanilla: "原版",
};

/** 从 MC 游戏版本提取大版本：1.21.1 → 1.21；24w14a → 24W；其余原样 */
function majorOf(gv: string): string {
  const m = gv.match(/^(\d+\.\d+)/);
  if (m) return m[1];
  const w = gv.match(/^(\d{2})w/i);
  if (w) return `${w[1]}W`;
  return gv;
}

/** 是否为快照/预发布版本：周快照（24w14a）、预发布（1.21-pre1 / -rc1） */
function isSnapshotVersion(gv: string): boolean {
  if (/^(\d{2})w/i.test(gv)) return true;
  return /-(pre|rc)\d*$/i.test(gv) || /^(pre|rc)\d*$/i.test(gv);
}

/** 数值化版本比较：1.9 < 1.10 < 1.21 */
function cmpVersion(a: string, b: string): number {
  const pa = a.split(/[^0-9]+/).filter(Boolean).map(Number);
  const pb = b.split(/[^0-9]+/).filter(Boolean).map(Number);
  const n = Math.max(pa.length, pb.length);
  for (let i = 0; i < n; i++) {
    const x = pa[i] ?? 0;
    const y = pb[i] ?? 0;
    if (x !== y) return x - y;
  }
  return 0;
}

/** 详情页版本树：大版本 → (加载器 · 小版本) → 文件 */
interface DetailGroup {
  major: string;
  combos: { key: string; loader: string; minor: string; files: ModFile[] }[];
}

/** MC 百科搜索地址 */
function wikiUrl(title: string): string {
  return `https://search.mcmod.cn/s?key=${encodeURIComponent(title)}`;
}

/** 资源分类 → Modrinth 官网路径段 */
const MODRINTH_TYPE: Record<string, string> = {
  mod: "mod",
  modpack: "modpack",
  datapack: "datapack",
  shader: "shader",
  resourcepack: "resourcepack",
};

/** 资源分类 → CurseForge 官网路径段 */
const CF_SECTION: Record<string, string> = {
  mod: "mc-mods",
  modpack: "modpacks",
  datapack: "data-packs",
  shader: "shaders",
  resourcepack: "texture-packs",
};

/** 平台官网地址（有 slug 时才可打开） */
function siteUrls(
  hit: ModSearchHit,
  category: string,
): { modrinth: string | null; curseforge: string | null } {
  return {
    modrinth:
      hit.platform === "modrinth" && hit.slug
        ? `https://modrinth.com/${MODRINTH_TYPE[category] ?? "mod"}/${hit.slug}`
        : null,
    curseforge:
      hit.platform === "curseforge" && hit.slug
        ? `https://www.curseforge.com/minecraft/${CF_SECTION[category] ?? "mc-mods"}/${hit.slug}`
        : null,
  };
}

interface ModDetailProps {
  hit: ModSearchHit;
  /** 下载平台（用于版本文件拉取与任务键前缀） */
  platform: string;
  /** 当前资源分类（拼平台官网地址） */
  category: string;
  /** 进入详情时生效的 MC 版本筛选（"" = 不限版本 → 折叠全部版本） */
  initialVersion: string;
  /** 退场动画进行中（返回列表时播放收起动画，卸载由容器兜底） */
  leaving: boolean;
  /** 进行中的下载任务（键 = mod:{platform}:{fileId}） */
  jobs: Record<string, ProgressPayload>;
  onBack: () => void;
  onDownload: (file: ModFile) => void;
  onCancel: (jobKey: string) => void;
  onOpenWiki: (hit: ModSearchHit) => void;
  onOpenSite: (url: string) => void;
}

export function ModDetail({
  hit,
  platform,
  category,
  initialVersion,
  leaving,
  jobs,
  onBack,
  onDownload,
  onCancel,
  onOpenWiki,
  onOpenSite,
}: ModDetailProps) {
  const [files, setFiles] = useState<ModFile[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  /** 版本视图：正式版 / 快照版 分页（按进入时的版本筛选预选） */
  const [view, setView] = useState<"release" | "snapshot">(
    initialVersion && isSnapshotVersion(initialVersion) ? "snapshot" : "release",
  );
  /** 激活的大版本（null = 未选择；选定版本进入时定位对应组） */
  const [activeMajor, setActiveMajor] = useState<string | null>(
    initialVersion ? majorOf(initialVersion) : null,
  );
  /** 进入时不限版本 → 折叠全部版本；选定版本 → 展开对应大版本 */
  const [collapseAll] = useState(initialVersion === "");
  /** 加载器·小版本行折叠状态（默认展开） */
  const [comboCollapsed, setComboCollapsed] = useState<Record<string, boolean>>({});

  const reload = () => {
    setLoading(true);
    setLoadError(null);
    // 忽略工具栏筛选，加载该项目全部版本文件（大版本分组展示）
    listModVersions(platform, hit.id, "", "")
      .then(setFiles)
      .catch((e) => setLoadError(friendlyError(e)))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hit.id, platform]);

  /** 版本树：分「正式版/快照版」两页；每页：大版本（新→旧）→ (加载器·小版本)（小版本新→旧）→ 文件 */
  const groups = useMemo<{
    release: DetailGroup[];
    snapshot: DetailGroup[];
  }>(() => {
    type ComboMap = Map<string, { loader: string; minor: string; files: ModFile[] }>;
    const majorMaps: { release: Map<string, ComboMap>; snapshot: Map<string, ComboMap> } = {
      release: new Map(),
      snapshot: new Map(),
    };
    for (const f of files) {
      const loaders = f.loaders.length > 0 ? f.loaders : ["vanilla"];
      for (const gv of f.game_versions.filter(Boolean)) {
        const kind = isSnapshotVersion(gv) ? "snapshot" : "release";
        const major = majorOf(gv);
        const comboMap = majorMaps[kind].get(major) ?? new Map();
        majorMaps[kind].set(major, comboMap);
        for (const ld of loaders) {
          const cKey = `${major}\u0000${ld}\u0000${gv}`;
          let combo = comboMap.get(cKey);
          if (!combo) {
            combo = { loader: ld, minor: gv, files: [] };
            comboMap.set(cKey, combo);
          }
          combo.files.push(f);
        }
      }
    }
    const toGroups = (majorMap: Map<string, ComboMap>): DetailGroup[] => {
      const result: DetailGroup[] = [];
      for (const [major, comboMap] of majorMap) {
        const combos = [...comboMap.entries()]
          .map(([key, c]) => ({ key, loader: c.loader, minor: c.minor, files: c.files }))
          .sort((a, b) => cmpVersion(b.minor, a.minor) || a.loader.localeCompare(b.loader));
        result.push({ major, combos });
      }
      result.sort((a, b) => cmpVersion(b.major, a.major));
      return result;
    };
    return { release: toGroups(majorMaps.release), snapshot: toGroups(majorMaps.snapshot) };
  }, [files]);

  // 生效视图：当前视图无内容时回退到正式版（仅含快照的 mod 则回退到快照版）
  const hasRel = groups.release.length > 0;
  const hasSnap = groups.snapshot.length > 0;
  const effView: "release" | "snapshot" =
    groups[view].length > 0 ? view : hasRel ? "release" : "snapshot";
  const viewGroups = groups[effView];
  // 展开的大版本：选定版本搜索进入时定位对应组；不限版本进入时全部折叠，点击标签后再展开
  const effGroup =
    activeMajor != null
      ? viewGroups.find((g) => g.major === activeMajor) ??
        (!collapseAll ? viewGroups[0] : undefined)
      : undefined;
  const sites = siteUrls(hit, category);

  /** 单个文件行（含下载进度 / 取消） */
  const renderFileRow = (f: ModFile) => {
    const key = `mod:${platform}:${f.id}`;
    const job = jobs[key];
    return (
      <div class={styles.fileRow} key={f.id}>
        <div class={styles.fileMain}>
          <span class={styles.fileVer}>{f.version || f.file_name}</span>
          <span class={styles.fileMeta}>
            {f.file_name} · {fmtBytes(f.size)}
            {f.game_versions.length > 0 &&
              ` · ${f.game_versions.slice(0, FILE_GV_PREVIEW).join(" / ")}`}
          </span>
          {job && (
            <ProgressBar
              className={styles.fileProgress}
              percentage={job.percentage}
              stage={job.stage || "下载中…"}
              speedBps={job.speed_bytes_per_sec}
              downloadedBytes={job.downloaded_bytes}
              totalBytes={job.total_bytes}
              etaSecs={job.eta_secs}
              label={`下载 ${f.file_name}`}
            />
          )}
        </div>
        <div class={styles.fileActions}>
          {job ? (
            <Button
              variant="danger"
              size="sm"
              onClick={() => onCancel(key)}
              title="取消将丢弃已下载的进度"
            >
              取消
            </Button>
          ) : (
            <Button variant="primary" size="sm" onClick={() => onDownload(f)}>
              下载
            </Button>
          )}
        </div>
      </div>
    );
  };

  return (
    <div class={`${styles.detail} ${leaving ? styles.leaving : ""}`}>
      <div class={styles.topBar}>
        <Button variant="subtle" onClick={onBack}>
          <Icon icon={ChevronLeft} size={16} strokeWidth={1.8} />
          返回列表
        </Button>
        <div class={styles.linkBtns}>
          <Button
            variant="standard"
            onClick={() => onOpenWiki(hit)}
            title="在浏览器中打开 MC 百科（mcmod.cn）资源详情页"
          >
            MC 百科
          </Button>
          {sites.modrinth && (
            <Button
              variant="standard"
              onClick={() => onOpenSite(sites.modrinth!)}
              title="在浏览器中打开 Modrinth 项目页"
            >
              Modrinth
            </Button>
          )}
          {sites.curseforge && (
            <Button
              variant="standard"
              onClick={() => onOpenSite(sites.curseforge!)}
              title="在浏览器中打开 CurseForge 项目页"
            >
              CurseForge
            </Button>
          )}
        </div>
      </div>

      <div class={styles.hero}>
        <ModIcon
          url={hit.icon_url}
          seed={`${hit.platform}:${hit.id}`}
          letter={placeholderLetter(hit.title_zh || hit.title)}
          className={styles.heroIcon}
        />
        <div class={styles.heroInfo}>
          <div class={styles.titleRow}>
            <h2 class={styles.title}>{hit.title_zh || hit.title}</h2>
            <Tag variant="accent">{hit.platform === "modrinth" ? "Modrinth" : "CurseForge"}</Tag>
          </div>
          <div class={styles.meta}>
            作者 {hit.author || "未知"} · 下载 {fmtNum(hit.downloads)}
          </div>
          <p class={styles.desc}>{hit.description_zh || hit.description || "（暂无简介）"}</p>
        </div>
      </div>

      <div class={styles.body}>
        <div class={styles.filesHead}>
          <h3 class={styles.filesTitle}>选择版本下载</h3>
          <p class={styles.filesCaption}>列出该项目全部版本，不受搜索时所选的版本与加载器筛选影响</p>
        </div>

        {loading && (
          <EmptyState
            className={styles.bodyStatus}
            icon={<Spinner size="sm" />}
            title="正在加载版本列表"
          />
        )}
        {!loading && loadError && (
          <EmptyState
            className={styles.bodyStatus}
            error
            title="版本列表加载失败"
            desc={loadError}
            action={<Button onClick={reload}>重试</Button>}
          />
        )}
        {!loading && !loadError && !hasRel && !hasSnap && (
          <EmptyState
            className={styles.bodyStatus}
            title="该项目暂无可用版本"
            desc="该资源没有可下载的版本文件"
          />
        )}

        {!loading && !loadError && (hasRel || hasSnap) && (
          <>
            {hasRel && hasSnap && (
              <div class={styles.seg} role="tablist" aria-label="版本类型">
                <button
                  type="button"
                  role="tab"
                  aria-selected={effView === "release"}
                  class={`${styles.segItem} ${effView === "release" ? styles.segItemActive : ""}`}
                  onClick={() => setView("release")}
                >
                  正式版
                </button>
                <button
                  type="button"
                  role="tab"
                  aria-selected={effView === "snapshot"}
                  class={`${styles.segItem} ${effView === "snapshot" ? styles.segItemActive : ""}`}
                  onClick={() => setView("snapshot")}
                >
                  快照版
                </button>
              </div>
            )}

            <div class={styles.vgBar} role="tablist" aria-label="大版本">
              {viewGroups.map((g) => {
                const active = effGroup != null && g.major === effGroup.major;
                return (
                  <button
                    type="button"
                    role="tab"
                    aria-selected={active}
                    class={`${styles.vgTab} ${active ? styles.vgTabActive : ""}`}
                    onClick={() => setActiveMajor(g.major)}
                    key={g.major}
                  >
                    <span class={styles.vgName}>{g.major}</span>
                    <span class={styles.vgCount}>{g.combos.length}</span>
                  </button>
                );
              })}
            </div>

            {effGroup && (
              <div class={styles.vgBody}>
                {effGroup.combos.map((c) => {
                  const cOpen =
                    comboCollapsed[c.key] === undefined
                      ? !collapseAll
                      : !comboCollapsed[c.key];
                  return (
                    <div class={styles.combo} key={c.key}>
                      <button
                        type="button"
                        class={`${styles.comboHead} ${cOpen ? styles.comboHeadOpen : ""}`}
                        onClick={() =>
                          setComboCollapsed((prev) => ({
                            ...prev,
                            [c.key]: cOpen,
                          }))
                        }
                        aria-expanded={cOpen}
                      >
                        <span class={styles.comboLoader}>{LOADER_LABEL[c.loader] ?? c.loader}</span>
                        <span class={styles.comboMinor}>{c.minor}</span>
                        <Icon
                          icon={cOpen ? ChevronUp : ChevronDown}
                          size={16}
                          strokeWidth={1.8}
                          class={styles.comboCaret}
                        />
                      </button>
                      <div class={`${styles.comboCollapse} ${cOpen ? styles.comboCollapseOpen : ""}`}>
                        <div class={styles.comboFiles}>{c.files.map(renderFileRow)}</div>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
