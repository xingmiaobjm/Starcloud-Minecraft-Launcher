// ConfirmDialog：确认对话框组件，替换浏览器原生 confirm()
// 危险操作自动聚焦「取消」防误触；Esc=取消；焦点圈定在对话框内

import type { ComponentChildren } from "preact";
import { useEffect, useRef } from "preact/hooks";
import { Modal } from "./Modal/Modal";
import { Button } from "./Button/Button";
import styles from "./ConfirmDialog.module.css";

interface Props {
  open: boolean;
  title: string;
  message: ComponentChildren;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = "确认",
  cancelLabel = "取消",
  danger = false,
  onConfirm,
  onCancel,
}: Props) {
  const cancelRef = useRef<HTMLButtonElement>(null);

  // 危险操作：焦点落「取消」；普通操作：焦点落「确认」（Fluent 默认主按钮）
  useEffect(() => {
    if (!open) return;
    const t = window.setTimeout(() => {
      cancelRef.current?.focus();
    }, 0);
    return () => window.clearTimeout(t);
  }, [open, danger]);

  if (!open) return null;

  return (
    <Modal open={open} onClose={onCancel} title={title} width="380px">
      <div class={styles.message}>{message}</div>
      <div class={styles.actions}>
        <Button ref={cancelRef} variant="standard" onClick={onCancel}>
          {cancelLabel}
        </Button>
        <Button variant={danger ? "danger" : "primary"} onClick={onConfirm}>
          {confirmLabel}
        </Button>
      </div>
    </Modal>
  );
}
