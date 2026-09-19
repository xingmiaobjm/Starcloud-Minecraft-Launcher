import styles from "./Switch.module.css";

interface SwitchProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  "aria-label"?: string;
  className?: string;
}

/** Fluent 开关：40×20 轨道 + 滑动圆点 */
export function Switch({
  checked,
  onChange,
  disabled = false,
  "aria-label": ariaLabel,
  className,
}: SwitchProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={ariaLabel}
      disabled={disabled}
      class={`${styles.switch} ${checked ? styles.on : ""} ${className ?? ""}`}
      onClick={() => onChange(!checked)}
    >
      <span class={styles.knob} />
    </button>
  );
}
