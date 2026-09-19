// Mods 页拆分模块（ModList / ModDetail）的共享小工具

/** 大数字紧凑显示：999 → "999"，1200 → "1.2K"，12345678 → "12.3M" */
export function fmtNum(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

/** 占位字母：标题首字符大写（中文标题取首字，原文返回） */
export function placeholderLetter(title: string): string {
  return title.trim().charAt(0).toUpperCase();
}
