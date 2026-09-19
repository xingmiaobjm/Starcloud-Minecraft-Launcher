import type { JSX } from "preact";
import { useId } from "preact/hooks";
import styles from "./Input.module.css";

interface InputProps extends JSX.InputHTMLAttributes<HTMLInputElement> {
  /** 字段标签（自动关联到输入框，读屏可读） */
  label?: string;
  /** 校验错误提示（显示于输入框下方并标记 aria-invalid） */
  error?: string;
  /** 行内提示（如输入格式说明） */
  hint?: string;
}

export function Input({ label, error, hint, className, id, ...rest }: InputProps) {
  const autoId = useId();
  const inputId = id ?? `in-${autoId}`;

  return (
    <div class={`${styles.wrapper} ${className ?? ""}`}>
      {label && (
        <label class={styles.label} for={inputId}>
          {label}
        </label>
      )}
      <input
        class={`${styles.input} ${error ? styles.inputError : ""}`}
        id={inputId}
        aria-invalid={error ? "true" : undefined}
        aria-describedby={error ? `${inputId}-err` : hint ? `${inputId}-hint` : undefined}
        {...rest}
      />
      {hint && !error && (
        <div class={styles.hint} id={`${inputId}-hint`}>
          {hint}
        </div>
      )}
      {error && (
        <div class={styles.error} id={`${inputId}-err`} role="alert">
          {error}
        </div>
      )}
    </div>
  );
}
