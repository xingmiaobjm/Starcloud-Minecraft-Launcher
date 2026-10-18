// Minecraft 草方块 3D 图标（anime.js 驱动：等轴立方体持续旋转 + 轻微摆动）
// 六面纹理用 CSS 渐变还原官方质感：顶面草皮、侧面草缘+泥土、底面泥土。
import { useEffect, useRef } from "preact/hooks";
import anime from "animejs";

interface Props {
  /** 图标尺寸（px） */
  size?: number;
  className?: string;
}

const FACES: { cls: string; rot: string }[] = [
  { cls: "gb3d-top", rot: "rotateX(90deg)" },
  { cls: "gb3d-side", rot: "" }, // 前
  { cls: "gb3d-side", rot: "rotateY(180deg)" }, // 后
  { cls: "gb3d-side", rot: "rotateY(90deg)" }, // 右
  { cls: "gb3d-side", rot: "rotateY(-90deg)" }, // 左
  { cls: "gb3d-bottom", rot: "rotateX(-90deg)" }, // 底
];

export function GrassBlock3D({ size = 64, className = "" }: Props) {
  const cubeRef = useRef<HTMLDivElement>(null);
  const half = size / 2;

  useEffect(() => {
    const cube = cubeRef.current;
    if (!cube) return;
    // 尊重「减弱动态」设置（--anim-scale: 0 时静置为静态等轴方块）
    const scale = parseFloat(getComputedStyle(cube).getPropertyValue("--anim-scale"));
    if (Number.isFinite(scale) && scale === 0) return;
    const anim = anime({
      targets: cube,
      rotateY: { value: "+=360", duration: 9000, easing: "linear", loop: true },
      rotateX: { value: [-22, -26, -22], duration: 4500, easing: "easeInOutQuad", loop: true },
      translateY: { value: [-1.5, 1.5], duration: 2200, easing: "easeInOutQuad", loop: true, direction: "alternate" },
    });
    return () => anim.pause();
  }, []);

  return (
    <div class={`gb3d ${className}`} style={{ width: size, height: size }} aria-hidden="true">
      <div
        ref={cubeRef}
        class="gb3d-cube"
        style={{
          width: size,
          height: size,
          transformStyle: "preserve-3d",
          transform: "rotateX(-22deg) rotateY(45deg)",
        }}
      >
        {FACES.map((f, i) => (
          <div
            key={i}
            class={`gb3d-face ${f.cls}`}
            style={{
              width: size,
              height: size,
              transform: `${f.rot} translateZ(${half}px)`,
            }}
          />
        ))}
      </div>
    </div>
  );
}
