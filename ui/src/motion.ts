// GSAP 动画工具 —— Fluent Native 动效
// 原则：只动画 transform/opacity；全部动画包裹 prefers-reduced-motion；
// 时长遵循 --anim-scale（时长 = 基准 / 倍率，0 = 关闭）；
// 每个入口返回 gsap.matchMedia 实例，由调用方在卸载时 mm.revert() 清理。

import { gsap } from "gsap";

/** Fluent 标准缓动 */
const EASE = "power2.out";

/**
 * 读取用户动画倍率（来自 CSS 变量 --anim-scale，由设置页写入）。
 * 倍率 = 速度倍率：0.1X 为 1/10 速度（慢），2X 为两倍速度（快），1.0 正常；
 * 0 表示关闭动画。读取失败时按 1.0。
 */
export function getAnimScale(): number {
  const raw = getComputedStyle(document.documentElement).getPropertyValue("--anim-scale");
  const v = parseFloat(raw);
  return Number.isFinite(v) ? v : 1;
}

/** 动画时长按用户倍率缩放（时长 = 基准 / 倍率；0 = 关闭，直接返回 0） */
function scaleDur(d: number): number {
  const s = getAnimScale();
  return s <= 0 ? 0 : d / s;
}

/**
 * 应用动画设置到全局：
 * - 写入 CSS 变量 --anim-scale（供 CSS calc() 与 GSAP 读取）
 * - scale <= 0 时给 <html> 加 no-anim class，强制所有 CSS 过渡/动画归零
 */
export function applyAnimScale(scale: number) {
  const root = document.documentElement;
  root.style.setProperty("--anim-scale", String(scale));
  root.classList.toggle("no-anim", scale <= 0);
}

/**
 * 页面切换入场：容器淡入 + 上移，页面内关键元素级联揭示。
 * 在 content 容器上调用（每次 page 变化重放）。
 * 级联目标 = 页面根容器（content 的第一个子元素）的直接子元素。
 */
export function pageEnter(root: HTMLElement): gsap.MatchMedia {
  const mm = gsap.matchMedia();
  mm.add("(prefers-reduced-motion: no-preference)", () => {
    if (getAnimScale() <= 0) return;
    const pageRoot = root.firstElementChild as HTMLElement | null;
    const targets = pageRoot
      ? Array.from(pageRoot.children).filter(
          (el): el is HTMLElement => el instanceof HTMLElement && !el.hidden,
        )
      : [];
    gsap.fromTo(
      root,
      { autoAlpha: 0, y: 8 },
      { autoAlpha: 1, y: 0, duration: scaleDur(0.3), ease: EASE, clearProps: "all" },
    );
    if (targets.length > 0) {
      // 总级联时长封顶：子元素极多的页面（如设置页）不至于拖沓
      const stagger = Math.min(scaleDur(0.04), scaleDur(0.4) / Math.max(targets.length, 1));
      gsap.fromTo(
        targets,
        { autoAlpha: 0, y: 8 },
        {
          autoAlpha: 1,
          y: 0,
          duration: scaleDur(0.4),
          ease: EASE,
          stagger,
          delay: scaleDur(0.1),
          clearProps: "all",
        },
      );
    }
  });
  return mm;
}

/** 状态点辉光 pulse（running / installing 时；color 为 rgb 三元组） */
export function statusDotPulse(dot: HTMLElement, color = "0,103,192"): gsap.MatchMedia {
  const mm = gsap.matchMedia();
  mm.add("(prefers-reduced-motion: no-preference)", () => {
    if (getAnimScale() <= 0) return;
    gsap.to(dot, {
      boxShadow: `0 0 10px rgba(${color},0.7)`,
      repeat: -1,
      yoyo: true,
      duration: scaleDur(0.8),
      ease: "sine.inOut",
    });
  });
  return mm;
}

/**
 * 分组展开/收起（手风琴）动画 —— 下载页分组行使用。
 *
 * - 展开：高度 0→auto + 淡入，内部行按序级联上移揭示（总级联时长封顶）；
 * - 收起：行先淡出下移，容器再合拢（退出比进入更快）；
 * - 快速连点可中断：先 killTweensOf 再执行新动画，不会卡在半开状态；
 * - 尊重 prefers-reduced-motion 与 --anim-scale（0 = 直接切换可见性）。
 *
 * @param body 分组容器外层（负责裁剪与高度）
 * @param open 展开 true / 收起 false
 * @param rowSelector 内部行选择器（CSS Modules 下由调用方传入）
 */
export function groupToggle(body: HTMLElement, open: boolean, rowSelector?: string) {
  const rows = rowSelector ? Array.from(body.querySelectorAll<HTMLElement>(rowSelector)) : [];
  gsap.killTweensOf([body, ...rows]);

  const reduce =
    window.matchMedia("(prefers-reduced-motion: reduce)").matches || getAnimScale() <= 0;
  if (reduce) {
    gsap.set([body, ...rows], { clearProps: "all" });
    body.style.display = open ? "block" : "none";
    return;
  }

  // 收起方向先以行内样式维持可见，动画结束后再隐藏并清理行内样式
  body.style.display = "block";

  if (open) {
    gsap.fromTo(
      body,
      { height: 0, opacity: 0 },
      {
        height: "auto",
        opacity: 1,
        duration: scaleDur(0.25),
        ease: EASE,
        clearProps: "height,opacity",
      },
    );
    const stagger = Math.min(scaleDur(0.02), scaleDur(0.3) / Math.max(rows.length, 1));
    gsap.fromTo(
      rows,
      { autoAlpha: 0, y: 6 },
      {
        autoAlpha: 1,
        y: 0,
        duration: scaleDur(0.24),
        ease: EASE,
        stagger,
        delay: scaleDur(0.04),
        clearProps: "all",
      },
    );
  } else {
    gsap.to(rows, {
      autoAlpha: 0,
      y: -4,
      duration: scaleDur(0.1),
      ease: "power2.in",
      stagger: -0.005,
    });
    gsap.to(body, {
      height: 0,
      opacity: 0,
      duration: scaleDur(0.15),
      ease: "power2.in",
      delay: scaleDur(0.04),
      onComplete: () => {
        body.style.display = "none";
        gsap.set(body, { clearProps: "all" });
      },
    });
  }
}
