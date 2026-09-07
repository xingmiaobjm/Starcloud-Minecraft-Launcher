// 共享格式化工具（全应用唯一实现，替代各页面复制粘贴）

/** 字节数 → 可读字符串（B/KB/MB/GB/TB） */
export function fmtBytes(b: number | null | undefined): string {
  if (!b || b <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = b;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v >= 100 || i === 0 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

/** 速度（字节/秒）→ 可读字符串 */
export function fmtSpeed(bps: number | null | undefined): string {
  if (!bps || bps <= 0) return "—";
  return `${fmtBytes(bps)}/s`;
}

/** 剩余秒数 → 可读字符串 */
export function fmtEta(secs: number | null | undefined): string {
  if (secs == null || !Number.isFinite(secs) || secs < 0) return "—";
  if (secs < 1) return "即将完成";
  const s = Math.round(secs);
  if (s < 60) return `${s} 秒`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} 分 ${s % 60} 秒`;
  return `${Math.floor(m / 60)} 时 ${m % 60} 分`;
}

/** 时间戳（毫秒）→ 本地时间字符串 HH:MM:SS */
export function fmtTime(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

/** 内存 MB → 可读字符串（1024 → "1.0 GB"） */
export function fmtMemory(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(mb % 1024 === 0 ? 0 : 1)} GB`;
  return `${mb} MB`;
}

/**
 * Mod 图标占位色：把字符串哈希进一组和谐的低饱和背景色，
 * 让无图标的 Mod 占位符有差异化（不再是清一色灰 "M"）。
 */
const PLACEHOLDER_HUES = [8, 28, 42, 95, 150, 175, 205, 235, 262, 300, 335];

export function placeholderColor(seed: string): { bg: string; fg: string } {
  let h = 0;
  for (let i = 0; i < seed.length; i++) h = (h * 31 + seed.charCodeAt(i)) | 0;
  const hue = PLACEHOLDER_HUES[Math.abs(h) % PLACEHOLDER_HUES.length];
  const light = document.documentElement.dataset.theme !== "dark";
  return light
    ? { bg: `hsl(${hue} 60% 92%)`, fg: `hsl(${hue} 55% 38%)` }
    : { bg: `hsl(${hue} 30% 26%)`, fg: `hsl(${hue} 50% 78%)` };
}
