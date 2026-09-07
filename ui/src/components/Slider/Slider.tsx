import styles from "./Slider.module.css";

interface SliderProps {
  value: number;
  min: number;
  max: number;
  step?: number;
  onChange: (value: number) => void;
  onCommit?: (value: number) => void;
  disabled?: boolean;
  /** aria-label（无可见标签时提供） */
  ariaLabel?: string;
  className?: string;
}

/**
 * Fluent 滑块：4px 轨道 + 填充色 + 20px 圆点拇指。
 * 设置页与版本设置抽屉共用（统一 16GB/24GB 上限由调用方决定）。
 */
export function Slider({
  value,
  min,
  max,
  step = 1,
  onChange,
  onCommit,
  disabled = false,
  ariaLabel,
  className,
}: SliderProps) {
  const pct = max > min ? ((value - min) / (max - min)) * 100 : 0;

  return (
    <input
      type="range"
      class={`${styles.slider} ${className ?? ""}`}
      value={value}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      aria-label={ariaLabel}
      aria-valuenow={value}
      aria-valuemin={min}
      aria-valuemax={max}
      style={{ "--fill": `${pct}%` } as import("preact").JSX.CSSProperties}
      onInput={(e) => onChange(Number((e.target as HTMLInputElement).value))}
      onChange={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))}
    />
  );
}
