// Mod 图标：本地缓存 → 原图 CDN → MCIM 镜像 → 差异化字母占位。
// normalizeIcon 同时供 app.tsx 预热图标缓存使用（由 pages/Mods.tsx re-export）。

import { useEffect, useState } from "preact/hooks";
import { getCachedIcon } from "../../api";
import { placeholderColor } from "../../utils/format";
import styles from "./ModIcon.module.css";

/** Modrinth CDN 图标 → MCIM 国内镜像（cdn.modrinth.com → mod.mcimirror.top），提升国内可访问性 */
export function normalizeIcon(url: string | null | undefined): string {
  if (!url) return "";
  if (url.startsWith("https://cdn.modrinth.com/")) {
    return url.replace("https://cdn.modrinth.com/", "https://mod.mcimirror.top/");
  }
  return url;
}

interface ModIconProps {
  url: string | null | undefined;
  /** 占位色种子（平台 + 项目 id），保证无图标项目的颜色稳定且互不相同 */
  seed: string;
  /** 占位字母（标题首字符） */
  letter: string;
  className?: string;
}

/** 模组图标：本地缓存 → 原图 CDN → MCIM 镜像 → 差异化字母占位 */
export function ModIcon({ url, seed, letter, className }: ModIconProps) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!url) return;
    setFailed(false);
    // 立即用原图（CDN）渲染；后台下载成功后无缝切换到本地缓存路径（零网络延迟）
    setSrc(url);
    getCachedIcon(url)
      .then((path) => {
        if (path && path !== url) {
          import("@tauri-apps/api/core").then(({ convertFileSrc }) => {
            setSrc(convertFileSrc(path));
          });
        }
      })
      .catch(() => {});
  }, [url]);

  if (!url || failed || !src) {
    // 占位符：按 seed 哈希出差异化底色 + 标题首字母，无图标项目也可分辨
    const color = placeholderColor(seed);
    return (
      <span
        class={`${styles.fallback} ${className ?? ""}`}
        style={{ background: color.bg, color: color.fg }}
        aria-hidden="true"
      >
        {letter || "M"}
      </span>
    );
  }
  return (
    <img
      class={`${styles.icon} ${className ?? ""}`}
      src={src}
      alt=""
      loading="lazy"
      onError={() => {
        // 原图加载失败 → 回退 MCIM 镜像；镜像也失败则显示占位
        const mirror = normalizeIcon(url);
        if (src !== mirror) {
          setSrc(mirror);
        } else {
          setFailed(true);
        }
      }}
    />
  );
}
