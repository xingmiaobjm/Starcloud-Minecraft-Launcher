// GSAP 动画工具 —— 遵循 docs/DESIGN.md §6 动效规范
// 原则：只动画 transform/opacity；全部动画包裹 prefers-reduced-motion；
// 每个入口返回 gsap.matchMedia 实例，由调用方在卸载时 mm.revert() 清理。

import { gsap } from "gsap";

/** 统一缓动 */
const EASE = "power3.out";
const ITEM_EASE = "power3.out";

/**
 * 页面切换入场：容器淡入 + 上移，页面内关键元素级联揭示。
 * 在 content 容器上调用（每次 page 变化重放）。
 */
export function pageEnter(root: HTMLElement): gsap.MatchMedia {
  const mm = gsap.matchMedia();
  mm.add("(prefers-reduced-motion: no-preference)", () => {
    const targets = root.querySelectorAll<HTMLElement>(
      ".page > .page-title, .page > .page-desc, .page > .page-subtitle, " +
        ".page > .card, .page > .toolbar, .page > .notice, .page > .list-item, " +
        ".page > .list > .list-item, .page > .ms-login, .page > .mode-slider, " +
        ".page > .progress-wrap, .page > .drawer-backdrop, .page > .plugin-frame, " +
        ".home > .launch-panel, .page > .home",
    );
    gsap.fromTo(
      root,
      { autoAlpha: 0, y: 10 },
      { autoAlpha: 1, y: 0, duration: 0.35, ease: EASE, clearProps: "all" },
    );
    if (targets.length > 0) {
      gsap.fromTo(
        targets,
        { autoAlpha: 0, y: 10 },
        {
          autoAlpha: 1,
          y: 0,
          duration: 0.45,
          ease: ITEM_EASE,
          stagger: 0.045,
          delay: 0.12,
          clearProps: "all",
        },
      );
    }
  });
  return mm;
}

/** 列表入场：对列表项做级联揭示（列表刷新后调用） */
export function listReveal(container: HTMLElement): gsap.MatchMedia {
  const mm = gsap.matchMedia();
  mm.add("(prefers-reduced-motion: no-preference)", () => {
    const items = container.querySelectorAll<HTMLElement>(".list-item");
    if (items.length > 0) {
      gsap.fromTo(
        items,
        { autoAlpha: 0, y: 10 },
        {
          autoAlpha: 1,
          y: 0,
          duration: 0.4,
          ease: ITEM_EASE,
          stagger: 0.04,
          clearProps: "all",
        },
      );
    }
  });
  return mm;
}

/**
 * 启动按钮呼吸微光（perpetual micro-interaction）。
 * 从 CSS 默认辉光向更强辉光循环，yoyo 往复。返回 mm；卸载时 mm.revert()。
 */
export function launchBtnGlow(btn: HTMLElement): gsap.MatchMedia {
  const mm = gsap.matchMedia();
  mm.add("(prefers-reduced-motion: no-preference)", () => {
    gsap.to(btn, {
      boxShadow:
        "0 1px 2px rgba(0,0,0,0.3), 0 6px 18px rgba(27,217,106,0.22), 0 0 24px rgba(27,217,106,0.32)",
      repeat: -1,
      yoyo: true,
      duration: 1.6,
      ease: "sine.inOut",
    });
  });
  return mm;
}

/** 状态点辉光 pulse（running / installing 时） */
export function statusDotPulse(dot: HTMLElement): gsap.MatchMedia {
  const mm = gsap.matchMedia();
  mm.add("(prefers-reduced-motion: no-preference)", () => {
    gsap.to(dot, {
      boxShadow: "0 0 14px rgba(27,217,106,0.85)",
      repeat: -1,
      yoyo: true,
      duration: 0.7,
      ease: "sine.inOut",
    });
  });
  return mm;
}
