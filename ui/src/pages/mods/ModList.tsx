// 模组资源结果列表：Fluent 卡片行（图标 + 标题（title_zh 优先）+ 平台标签 + 两行简介 + 作者/下载量）。
// 空态/失败态分离：搜索失败可重试；无匹配可清除筛选；热门缓存失败可一键搜索。

import type { Ref } from "preact";
import { ChevronRight } from "lucide";
import { Button } from "../../components/Button/Button";
import { EmptyState } from "../../components/EmptyState/EmptyState";
import { Icon } from "../../components/Icon/Icon";
import { Spinner } from "../../components/Spinner/Spinner";
import { Tag } from "../../components/Tag/Tag";
import type { ModSearchHit } from "../../types";
import { ModIcon } from "./ModIcon";
import { fmtNum, placeholderLetter } from "./shared";
import styles from "./ModList.module.css";

interface ModListProps {
  hits: ModSearchHit[];
  /** 首屏搜索进行中 */
  searching: boolean;
  /** 已执行过至少一次搜索/推荐加载 */
  searched: boolean;
  /** 搜索失败信息（非 null 显示失败态 + 重试；追加翻页失败走页面 Notice，不在此显示） */
  error: string | null;
  /** 热门缓存加载失败（尚未搜索时显示一键搜索入口） */
  popularError: boolean;
  loadingMore: boolean;
  hasMore: boolean;
  /** 当前搜索关键词（区分「无匹配」与「暂无推荐」文案） */
  query: string;
  scrollRef: Ref<HTMLDivElement>;
  onScroll: () => void;
  onOpen: (hit: ModSearchHit) => void;
  /** 搜索失败重试 */
  onRetry: () => void;
  /** 一键搜索（热门缓存失败时的联网搜索入口） */
  onSearch: () => void;
  /** 清除筛选（关键词 + 版本 + 加载器 + 类型标签） */
  onClearFilters: () => void;
}

export function ModList({
  hits,
  searching,
  searched,
  error,
  popularError,
  loadingMore,
  hasMore,
  query,
  scrollRef,
  onScroll,
  onOpen,
  onRetry,
  onSearch,
  onClearFilters,
}: ModListProps) {
  const empty = hits.length === 0;

  return (
    <div class={styles.list} ref={scrollRef} onScroll={onScroll}>
      {searching && empty && (
        <EmptyState
          className={styles.status}
          icon={<Spinner />}
          title="正在搜索"
          desc="正在从 Modrinth 或 CurseForge 获取结果"
        />
      )}

      {!searching && error && empty && (
        <EmptyState
          className={styles.status}
          error
          title="搜索失败"
          desc={error}
          action={<Button onClick={onRetry}>重试</Button>}
        />
      )}

      {!searching && !error && searched && empty &&
        (query.trim() ? (
          <EmptyState
            className={styles.status}
            title={`未找到与「${query.trim()}」相关的资源`}
            desc="试试更换关键词，或清除筛选条件后重新搜索"
            action={<Button onClick={onClearFilters}>清除筛选</Button>}
          />
        ) : (
          <EmptyState
            className={styles.status}
            title="暂无推荐内容"
            desc="热门资源列表为空，可输入关键词搜索"
          />
        ))}

      {!searching && !error && !searched && empty &&
        (popularError ? (
          <EmptyState
            className={styles.status}
            error
            title="热门推荐加载失败"
            desc="本地缓存读取失败，可联网搜索热门资源"
            action={<Button onClick={onSearch}>一键搜索</Button>}
          />
        ) : (
          <EmptyState
            className={styles.status}
            title="输入关键词搜索"
            desc="支持 Mod、整合包、数据包、光影包与材质包，留空关键词可查看热门推荐"
          />
        ))}

      {hits.map((h) => (
        <button
          type="button"
          class={styles.row}
          key={`${h.platform}:${h.id}`}
          onClick={() => onOpen(h)}
        >
          <ModIcon
            url={h.icon_url}
            seed={`${h.platform}:${h.id}`}
            letter={placeholderLetter(h.title_zh || h.title)}
            className={styles.icon}
          />
          <span class={styles.info}>
            <span class={styles.titleRow}>
              <span class={styles.title}>{h.title_zh || h.title}</span>
              <Tag variant="accent">
                {h.platform === "modrinth" ? "Modrinth" : "CurseForge"}
              </Tag>
            </span>
            <span class={styles.desc}>{h.description_zh || h.description || "（暂无简介）"}</span>
            <span class={styles.meta}>
              <span>作者 {h.author || "未知"}</span>
              <span aria-hidden="true">·</span>
              <span>下载 {fmtNum(h.downloads)}</span>
            </span>
          </span>
          <Icon
            icon={ChevronRight}
            size={16}
            strokeWidth={1.8}
            class={styles.chevron}
          />
        </button>
      ))}

      {loadingMore && (
        <div class={styles.more}>
          <Spinner size="sm" />
          加载更多…
        </div>
      )}
      {!loadingMore && searched && hits.length > 0 && !hasMore && (
        <div class={styles.more}>已加载全部</div>
      )}
    </div>
  );
}
