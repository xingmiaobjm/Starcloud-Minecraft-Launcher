import styles from "./Spinner.module.css";

type SpinnerSize = "sm" | "md" | "lg";

interface Props {
  size?: SpinnerSize;
  className?: string;
}

export function Spinner({ size = "md", className }: Props) {
  const classNames = [styles.spinner, styles[size], className].filter(Boolean).join(" ");

  return <span class={classNames} role="status" aria-label="加载中" />;
}
