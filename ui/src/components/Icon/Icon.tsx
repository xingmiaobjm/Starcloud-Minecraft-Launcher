// Icon —— Morphicons 图标组件（Fluent Native 图标体系）
// 基于 morphicons/element 自定义元素 + lucide 图标数据：
// - 图标以数据传入（Lucide IconNode 或原始 d 字符串），共享 24×24 网格
// - 同一位置 icon 变化时自动弹簧形变（morph），静止时棱角锐利
// - 首次赋值静态直绘，不播放入场动画
// - 无障碍：未传 label 时默认 aria-hidden

import { useEffect, useRef } from "preact/hooks";
import { defineMorphIcon } from "morphicons/element";
import type { IconInput, MorphIconElement } from "morphicons/element";

// 定义 <morph-icon>（幂等，多次调用安全）
defineMorphIcon();

// Preact JSX 认识 <morph-icon>（preact 的 JSX 命名空间挂在模块上）
declare module "preact" {
  namespace JSX {
    interface IntrinsicElements {
      "morph-icon": import("preact").JSX.HTMLAttributes<HTMLElement> & {
        size?: number | string;
        "stroke-width"?: number | string;
        label?: string;
      };
    }
  }
}

export interface IconProps {
  /** 图标数据：lucide 包导出的 IconNode（如 import { Play } from "lucide"）或原始 d 字符串 */
  icon: IconInput;
  /** 像素尺寸（默认 18，导航 20，标题栏 12） */
  size?: number;
  /** 描边宽度（默认 2，与 lucide 一致） */
  strokeWidth?: number;
  /** 可访问名称；提供后 role=img，否则 aria-hidden */
  label?: string;
  class?: string;
}

export function Icon({ icon, size = 18, strokeWidth = 2, label, class: cls }: IconProps) {
  const ref = useRef<MorphIconElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.reducedMotion = "user"; // 尊重系统"减少动画"设置
    // 控制器自行处理：首次赋值静态直绘，变化时弹簧形变，重复值跳过
    el.icon = icon;
  }, [icon]);

  return (
    <morph-icon
      ref={ref}
      class={cls}
      size={size}
      stroke-width={strokeWidth}
      label={label}
    />
  );
}
