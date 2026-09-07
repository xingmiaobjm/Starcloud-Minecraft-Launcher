// 模组资源页（容器）：Mod / 整合包 / 数据包 / 光影包 / 材质包
// 职责：状态管理 + 工具栏（分类/搜索/筛选/平台）+ 列表/详情切换 + 搜索分页 + 下载任务。
// 列表行与详情面板拆分至 ./mods/（ModList / ModDetail / ModIcon）。

import { useEffect, useRef, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import { ChevronDown, ChevronUp } from "lucide";
import {
  cancelInstall,
  downloadMod,
  listVersions,
  loadPopularMods,
  mcmodLookup,
  openUrl,
  searchMods,
  translateModDescriptions,
  translateModTitles,
} from "../api";
import { Button } from "../components/Button/Button";
import { Card } from "../components/Card/Card";
import { Input } from "../components/Input/Input";
import { MaybeNotice } from "../components/Notice/Notice";
import { Select } from "../components/Select/Select";
import { Icon } from "../components/Icon/Icon";
import { friendlyError } from "../errorMessages";
import { useNotice } from "../hooks/useNotice";
import type {
  InstallDonePayload,
  ModFile,
  ModSearchHit,
  ProgressPayload,
  VersionInfo,
} from "../types";
import { ModDetail } from "./mods/ModDetail";
import { ModList } from "./mods/ModList";
import styles from "./Mods.module.css";

// app.tsx 从本模块导入 normalizeIcon 预热图标缓存；实现位于 ./mods/ModIcon
export { normalizeIcon } from "./mods/ModIcon";

/** 资源分类：ID 与两个平台共用（Modrinth project_type / CurseForge classId 由后端映射） */
const CATEGORIES: { id: string; label: string }[] = [
  { id: "mod", label: "Mod" },
  { id: "modpack", label: "整合包" },
  { id: "datapack", label: "数据包" },
  { id: "shader", label: "光影包" },
  { id: "resourcepack", label: "材质包" },
];

/** Modrinth 类型标签（categories facet，仅 Modrinth 生效） */
const CATEGORY_TAGS: { value: string; label: string }[] = [
  { value: "", label: "不限类型" },
  { value: "magic", label: "魔法" },
  { value: "adventure", label: "冒险" },
  { value: "technology", label: "科技" },
  { value: "combat", label: "战斗" },
  { value: "equipment", label: "装备" },
  { value: "utility", label: "实用" },
  { value: "decoration", label: "装饰" },
  { value: "building", label: "建筑" },
  { value: "food", label: "食物" },
  { value: "storage", label: "储物" },
  { value: "redstone", label: "红石" },
  { value: "worldgen", label: "世界生成" },
  { value: "optimization", label: "性能优化" },
  { value: "management", label: "管理" },
  { value: "social", label: "社交" },
  { value: "biome", label: "生物群系" },
  { value: "mobs", label: "生物" },
  { value: "farming", label: "农业" },
  { value: "minigame", label: "小游戏" },
  { value: "cosmetic", label: "外观" },
  { value: "music", label: "音乐" },
  { value: "library", label: "库" },
  { value: "api", label: "API" },
  { value: "game-mechanics", label: "游戏机制" },
];

const LOADERS: { id: string; label: string }[] = [
  { id: "", label: "不限加载器" },
  { id: "fabric", label: "Fabric" },
  { id: "forge", label: "Forge" },
  { id: "quilt", label: "Quilt" },
  { id: "neoforge", label: "NeoForge" },
];

const PLATFORMS: { id: string; label: string }[] = [
  { id: "modrinth", label: "Modrinth" },
  { id: "curseforge", label: "CurseForge" },
];

/** 每页条数：与后端 search_mods 的 limit 固定值一致（src-tauri/src/commands.rs，Modrinth 上限 100 / CurseForge 上限 50） */
const PAGE_SIZE = 50;

/** 滚动加载提前量：距底部不足该距离时预取下一页 */
const SCROLL_PRELOAD_PX = 300;

/** 版本下拉最多提供的正式版条目数（完整清单过长，下拉支持输入过滤） */
const MAX_VERSION_OPTIONS = 80;

/** 详情退场动画兜底时长（ms）：与 ModDetail.module.css 的退场动画（--dur-slow = 0.3s 基准）保持一致 */
const DETAIL_LEAVE_MS = 300;

/** 新建下载任务占位进度 */
function freshJob(key: string): ProgressPayload {
  return {
    version_id: key,
    downloaded_bytes: 0,
    total_bytes: 0,
    percentage: 0,
    stage: "准备中…",
    status: "Pending",
    speed_bytes_per_sec: 0,
    eta_secs: null,
  };
}

/** MC 百科搜索地址 */
function wikiUrl(title: string): string {
  return `https://search.mcmod.cn/s?key=${encodeURIComponent(title)}`;
}

export function Mods() {
  const [platform, setPlatform] = useState("modrinth");
  const [category, setCategory] = useState("mod");
  const [query, setQuery] = useState("");
  const [mcVersion, setMcVersion] = useState("");
  const [loader, setLoader] = useState("");
  /** Modrinth 类型标签（如 magic/adventure，仅 Modrinth 生效） */
  const [tag, setTag] = useState("");
  const [versions, setVersions] = useState<VersionInfo[]>([]);
  const [hits, setHits] = useState<ModSearchHit[]>([]);
  const [searching, setSearching] = useState(false);
  const [searched, setSearched] = useState(false);
  /** 搜索失败信息（非 null 时列表显示失败态 + 重试） */
  const [searchError, setSearchError] = useState<string | null>(null);
  /** 热门缓存加载失败（尚未搜索时列表显示一键搜索入口） */
  const [popularError, setPopularError] = useState(false);
  /** 详情视图选中的项目（非 null 时显示详情） */
  const [selected, setSelected] = useState<ModSearchHit | null>(null);
  /** 详情视图退场动画进行中（返回列表时短暂保留视图播放收起动画） */
  const [leaving, setLeaving] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [hasMore, setHasMore] = useState(false);
  const [jobs, setJobs] = useState<Record<string, ProgressPayload>>({});
  const jobsRef = useRef<Record<string, ProgressPayload>>({});
  const { notice, show, clear } = useNotice();
  const leavingTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const offsetRef = useRef(0);
  const searchingRef = useRef(false);
  const loadingMoreRef = useRef(false);
  const hasMoreRef = useRef(false);
  /** 首屏搜索的生效条件（滚动翻页时沿用，避免中途改参数导致分页错位） */
  const searchParamsRef = useRef<{
    q: string;
    ver: string;
    loader: string;
    cat: string;
    tg: string;
    pf: string;
  }>({ q: "", ver: "", loader: "", cat: "mod", tg: "", pf: "modrinth" });
  /** 用户是否主动搜索过（主动搜索后，启动预加载完成不再覆盖搜索结果） */
  const manualSearchRef = useRef(false);
  /** 搜索会话序号：每次全新搜索递增；过期请求返回后自检丢弃结果（支持搜索途中重搜） */
  const sessionRef = useRef(0);

  const updateJobs = (
    fn: (prev: Record<string, ProgressPayload>) => Record<string, ProgressPayload>,
  ) => {
    setJobs((prev) => {
      const next = fn(prev);
      jobsRef.current = next;
      return next;
    });
  };

  // 进入页面立即加载缓存热门模组（启动时预加载，秒开）；
  // 缓存读取失败不再静默，列表给出可一键搜索的失败态。
  // 版本清单并行加载，仅用于工具栏下拉，不阻塞推荐展示。
  useEffect(() => {
    listVersions()
      .then(setVersions)
      .catch(() => {});

    let cancelled = false;
    const unsubs: (() => void)[] = [];

    const applyPopularCache = (cache: { mods: ModSearchHit[]; total_count: number } | null) => {
      if (!cache || manualSearchRef.current) return;
      setHits(cache.mods);
      setSearched(true);
      setPopularError(false);
      offsetRef.current = cache.mods.length;
      hasMoreRef.current = cache.total_count > cache.mods.length;
      setHasMore(cache.total_count > cache.mods.length);
      // 异步补译缓存中的标题
      applyZhAsync(cache.mods);
    };

    loadPopularMods()
      .then((cache) => {
        if (!cancelled) applyPopularCache(cache);
      })
      .catch(() => {
        if (!cancelled) setPopularError(true);
      });

    // 启动预加载完成后用最新缓存刷新列表（用户已主动搜索时不覆盖）
    if ("__TAURI_INTERNALS__" in window) {
      listen<boolean>("startup-preload-done", () => {
        loadPopularMods()
          .then((cache) => {
            if (!cancelled) applyPopularCache(cache);
          })
          .catch(() => {
            if (!cancelled) setPopularError(true);
          });
      }).then((u) => unsubs.push(u));
    }

    return () => {
      cancelled = true;
      unsubs.forEach((u) => u());
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
        if (p.success) show({ text: p.message || "下载完成", kind: "ok" });
        else show({ text: p.message || "下载失败", kind: "err" }, null);
      }
    }).then((u) => unsubs.push(u));

    return () => unsubs.forEach((u) => u());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // 卸载时清理详情退场动画兜底计时器
  useEffect(
    () => () => {
      if (leavingTimeoutRef.current) clearTimeout(leavingTimeoutRef.current);
    },
    [],
  );

  /** 搜索（关键词可空：空关键词 = 热门推荐；条件可覆盖；append=true 时为滚动加载下一页） */
  const doSearch = async (
    opts: {
      q?: string;
      cat?: string;
      tg?: string;
      ver?: string;
      loader?: string;
      pf?: string;
    } = {},
    append = false,
  ) => {
    const q = opts.q ?? query.trim();
    const cat = opts.cat ?? category;
    const tg = opts.tg ?? tag;
    const ver = opts.ver ?? mcVersion;
    const ldr = opts.loader ?? loader;
    const pf = opts.pf ?? platform;
    // 首屏记录本次生效的完整搜索条件；滚动翻页沿用首屏条件，
    // 避免加载下一页时误用中途改动过的工具栏参数导致分页错位
    const eff = append
      ? searchParamsRef.current
      : (searchParamsRef.current = { q, ver, loader: ldr, cat, tg, pf });
    const off = append ? offsetRef.current : 0;
    // 全新搜索递增会话序号，旧请求返回后发现过期会丢弃结果（搜索途中可再次发起）
    const session = append ? sessionRef.current : ++sessionRef.current;
    if (!append) {
      manualSearchRef.current = true;
      setSearched(true);
      setSelected(null);
      clear();
      setSearchError(null);
      setHits([]);
      offsetRef.current = 0;
      hasMoreRef.current = true;
      loadingMoreRef.current = false;
      setLoadingMore(false);
    }
    searchingRef.current = true;
    setSearching(true);
    try {
      const results = await searchMods(eff.q, eff.ver, eff.loader, eff.cat, eff.tg, eff.pf, off);
      if (session !== sessionRef.current) return; // 已被更新的搜索取代
      setHits((prev) => (append ? [...prev, ...results] : results));
      offsetRef.current = off + results.length;
      // 后端固定每页 PAGE_SIZE 条：不足一页即已到底
      const more = results.length >= PAGE_SIZE;
      hasMoreRef.current = more;
      setHasMore(more);
      // 异步补译：搜索立即展示（先英文后中文），翻译不阻塞滚动加载
      applyZhAsync(results);
    } catch (e) {
      if (session !== sessionRef.current) return;
      if (!append) {
        // 首屏失败：列表显示失败态（EmptyState error + 重试）
        setHits([]);
        setSearchError(friendlyError(e));
      } else {
        // 翻页失败：列表已有内容，仅提示；已加载部分保留
        hasMoreRef.current = false;
        setHasMore(false);
        show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
      }
    } finally {
      if (session === sessionRef.current) {
        searchingRef.current = false;
        setSearching(false);
      }
    }
  };

  /** 异步补齐一批搜索结果的 title_zh 与 description_zh：不阻塞滚动加载 */
  const applyZhAsync = (results: ModSearchHit[]) => {
    // 1. 标题翻译（内置词典 → MCIM → MC百科 → 机器翻译）
    const titles = results.map((r) => r.title);
    translateModTitles(titles)
      .then((zhs) => {
        const map = new Map<string, string>();
        results.forEach((r, i) => {
          const zh = zhs[i];
          if (zh && zh !== r.title) map.set(r.title, zh);
        });
        if (map.size === 0) return;
        setHits((prev) =>
          prev.map((h) => (map.has(h.title) ? { ...h, title_zh: map.get(h.title)! } : h)),
        );
      })
      .catch(() => {
        /* 翻译失败保持英文标题 */
      });

    // 2. 描述翻译（MCIM 翻译 API）
    const items: [string, string][] = results.map((r) => [r.platform, r.id]);
    translateModDescriptions(items)
      .then((zhs) => {
        const map = new Map<string, string>();
        results.forEach((r, i) => {
          const zh = zhs[i];
          if (zh && zh !== r.description) map.set(r.id, zh);
        });
        if (map.size === 0) return;
        setHits((prev) =>
          prev.map((h) => (map.has(h.id) ? { ...h, description_zh: map.get(h.id)! } : h)),
        );
      })
      .catch(() => {
        /* 翻译失败保持英文描述 */
      });
  };

  /** 滚动加载下一页：接近底部（剩余 SCROLL_PRELOAD_PX）时提前加载，划到底已续上 */
  const handleListScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - SCROLL_PRELOAD_PX) {
      loadMore();
    }
  };

  /** 加载下一页（防重入） */
  const loadMore = () => {
    if (searchingRef.current || loadingMoreRef.current || !hasMoreRef.current) return;
    loadingMoreRef.current = true;
    setLoadingMore(true);
    doSearch({}, true).finally(() => {
      loadingMoreRef.current = false;
      setLoadingMore(false);
    });
  };

  /** 切换资源分类：立即重新搜索 */
  const changeCategory = (id: string) => {
    setCategory(id);
    doSearch({ cat: id });
  };

  /** 切换 Modrinth 类型标签：立即重新搜索 */
  const changeTag = (id: string) => {
    setTag(id);
    doSearch({ tg: id });
  };

  /** 切换平台：保留条件，在目标平台重新搜索 */
  const changePlatform = (id: string) => {
    setPlatform(id);
    doSearch({ pf: id });
  };

  /** 清除筛选：重置关键词与版本/加载器/类型筛选（保留平台与资源分类）后重新搜索 */
  const clearFilters = () => {
    setQuery("");
    setMcVersion("");
    setLoader("");
    setTag("");
    doSearch({ q: "", ver: "", loader: "", tg: "" });
  };

  /** 点击项目进入详情（版本列表由详情面板自行加载） */
  const openDetail = (hit: ModSearchHit) => {
    // 取消可能还在倒计时的退场兜底，避免刚打开的详情被误卸载
    if (leavingTimeoutRef.current) {
      clearTimeout(leavingTimeoutRef.current);
      leavingTimeoutRef.current = null;
    }
    clear();
    setLeaving(false);
    setSelected(hit);
  };

  /** 返回搜索结果列表（先播收起动画，兜底计时后卸载详情） */
  const backToList = () => {
    if (leaving) return;
    clear();
    setLeaving(true);
    // 兜底卸载：动画关闭（--anim-scale=0）时 animationend 不会触发
    if (leavingTimeoutRef.current) clearTimeout(leavingTimeoutRef.current);
    leavingTimeoutRef.current = setTimeout(() => {
      setSelected(null);
      setLeaving(false);
    }, DETAIL_LEAVE_MS);
  };

  /** 打开 MC 百科：优先资源详情页（mcmod.cn/class/{id}.html），未收录时回退到搜索页 */
  const handleOpenWiki = async (hit: ModSearchHit) => {
    try {
      const classUrl = await mcmodLookup(hit.title);
      await openUrl(classUrl || wikiUrl(hit.title));
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    }
  };

  /** 打开平台官网（Modrinth / CurseForge） */
  const handleOpenSite = async (url: string) => {
    try {
      await openUrl(url);
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    }
  };

  const handleDownload = (hit: ModSearchHit, f: ModFile) => {
    const key = `mod:${platform}:${f.id}`;
    clear();
    updateJobs((prev) => ({ ...prev, [key]: freshJob(key) }));
    downloadMod({
      platform,
      fileId: f.id,
      fileName: f.file_name,
      url: f.url,
      size: f.size,
      title: hit.title,
    }).catch((e) => {
      // 任务未建立（不会有进度/完成事件来清理占位），移除占位进度
      updateJobs((prev) => {
        if (!(key in prev)) return prev;
        const next = { ...prev };
        delete next[key];
        return next;
      });
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    });
  };

  const handleCancel = async (key: string) => {
    try {
      await cancelInstall(key);
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) }, null);
    }
  };

  // ------------------------------------------------------------------
  // 详情视图：宽卡片（项目信息 + 平台/百科外链 + 版本树下载）
  // ------------------------------------------------------------------
  if (selected) {
    return (
      <div class={`page ${styles.page} ${styles.pageWide}`}>
        <MaybeNotice notice={notice} onClose={clear} />
        <ModDetail
          key={`${selected.platform}:${selected.id}`}
          hit={selected}
          platform={platform}
          category={category}
          initialVersion={searchParamsRef.current.ver}
          leaving={leaving}
          jobs={jobs}
          onBack={backToList}
          onDownload={(file) => handleDownload(selected, file)}
          onCancel={handleCancel}
          onOpenWiki={handleOpenWiki}
          onOpenSite={handleOpenSite}
        />
      </div>
    );
  }

  // ------------------------------------------------------------------
  // 列表视图：分类分段 + 搜索/筛选工具栏 + 结果列表
  // ------------------------------------------------------------------
  return (
    <div class={`page ${styles.page}`}>
      <header class="page-header">
        <h1 class="page-title">模组资源</h1>
        <p class="page-desc">
          搜索 Mod、整合包、数据包、光影包与材质包，点击项目查看详情并选择版本下载。
        </p>
      </header>

      <MaybeNotice notice={notice} onClose={clear} />

      <Card className={styles.searchCard}>
        <div class={`${styles.seg} ${styles.catBar}`} role="tablist" aria-label="资源分类">
          {CATEGORIES.map((c) => (
            <button
              key={c.id}
              type="button"
              role="tab"
              aria-selected={category === c.id}
              class={`${styles.segItem} ${category === c.id ? styles.segItemActive : ""}`}
              onClick={() => changeCategory(c.id)}
            >
              {c.label}
            </button>
          ))}
        </div>

        <div class={`page-toolbar ${styles.toolbar}`}>
          <Input
            className={styles.searchInput}
            placeholder="搜索模组或整合包名称（留空查看热门推荐）…"
            aria-label="搜索资源"
            value={query}
            onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") doSearch();
            }}
          />
          <Button
            variant="primary"
            onClick={() => doSearch()}
            busy={searching}
            title="搜索进行中也可再次发起新搜索"
          >
            {searching ? "搜索中" : "搜索"}
          </Button>
          <button
            type="button"
            class={`${styles.filtersToggle} ${filtersOpen ? styles.filtersToggleOpen : ""}`}
            onClick={() => setFiltersOpen((v) => !v)}
            aria-expanded={filtersOpen}
            title="高级筛选"
          >
            筛选
            <Icon icon={filtersOpen ? ChevronUp : ChevronDown} size={14} strokeWidth={1.8} />
          </button>
        </div>

        <div class={`${styles.filtersPanel} ${filtersOpen ? styles.filtersPanelOpen : ""}`}>
          <div class={styles.filtersInner}>
            <div class={styles.seg} role="tablist" aria-label="Mod 平台">
              {PLATFORMS.map((p) => (
                <button
                  key={p.id}
                  type="button"
                  role="tab"
                  aria-selected={platform === p.id}
                  class={`${styles.segItem} ${platform === p.id ? styles.segItemActive : ""}`}
                  onClick={() => changePlatform(p.id)}
                >
                  {p.label}
                </button>
              ))}
            </div>
            <Select
              value={mcVersion}
              options={[
                { value: "", label: "不限版本" },
                ...versions
                  .filter((v) => v.version_type.toLowerCase() === "release")
                  .slice(0, MAX_VERSION_OPTIONS)
                  .map((v) => ({ value: v.id, label: v.id })),
              ]}
              onChange={setMcVersion}
              placeholder="不限版本"
              filterable
              ariaLabel="Minecraft 版本"
            />
            <Select
              value={loader}
              options={LOADERS.map((l) => ({ value: l.id, label: l.label }))}
              onChange={setLoader}
              placeholder="不限加载器"
              ariaLabel="Mod 加载器"
            />
            {platform === "modrinth" && (
              <Select
                value={tag}
                options={CATEGORY_TAGS}
                onChange={changeTag}
                placeholder="不限类型"
                ariaLabel="Modrinth 类型标签"
              />
            )}
          </div>
        </div>
      </Card>

      {searched && query.trim() && <div class={styles.summary}>共 {hits.length} 个结果</div>}

      <ModList
        hits={hits}
        searching={searching}
        searched={searched}
        error={searchError}
        popularError={popularError}
        loadingMore={loadingMore}
        hasMore={hasMore}
        query={query}
        scrollRef={scrollRef}
        onScroll={handleListScroll}
        onOpen={openDetail}
        onRetry={() => doSearch()}
        onSearch={() => doSearch()}
        onClearFilters={clearFilters}
      />
    </div>
  );
}
