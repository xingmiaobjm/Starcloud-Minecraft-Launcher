// VersionSettingsDrawer：版本级启动设置抽屉（覆盖全局默认）
// 内存 / 语言 / 版本隔离 / Java / JVM 参数（高级设置）
// 未设置的字段跟随全局默认；点「恢复默认」可整体回到全局配置。

import { useEffect, useRef, useState } from "preact/hooks";
import { friendlyError } from "../errorMessages";
import { checkJava, getVersionSettings, listJavaRuntimes, setVersionSettings } from "../api";
import { useNotice } from "../hooks/useNotice";
import { fmtMemory } from "../utils/format";
import { Drawer } from "./Drawer/Drawer";
import { Select } from "./Select/Select";
import { Switch } from "./Switch/Switch";
import { Button } from "./Button/Button";
import { Input } from "./Input/Input";
import { Slider } from "./Slider/Slider";
import { MaybeNotice } from "./Notice/Notice";
import { Spinner } from "./Spinner/Spinner";
import { ConfirmDialog } from "./ConfirmDialog";
import type { JavaInfo, JavaRuntime, VersionSettings } from "../types";
import styles from "./VersionSettingsDrawer.module.css";

interface Props {
  open: boolean;
  /** 目标版本 ID */
  versionId: string;
  onClose: () => void;
}

const EMPTY: VersionSettings = {
  min_memory_mb: null,
  max_memory_mb: null,
  language: null,
  isolated: false,
  java_executable: null,
  jvm_args: [],
  use_builtin_jvm_args: null,
};

/** 内存滑块范围：0 - 16 GB，256 MB 步进（GB 显示由 fmtMemory 处理） */
const MEM_MIN = 0;
const MEM_MAX = 16384;
const MEM_STEP = 256;
/** 开启「自定义内存」时的缺省值 */
const MEM_DEFAULT_MIN = 2048;
const MEM_DEFAULT_MAX = 4096;

/** JVM 优化预设 */
const JVM_PRESETS: { value: string; label: string; args: string }[] = [
  {
    value: "builtin",
    label: "内置优化",
    args:
      "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 " +
      "-XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch " +
      "-XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M " +
      "-XX:G1ReservePercent=20 -XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 " +
      "-XX:InitiatingHeapOccupancyPercent=15 -XX:G1MixedGCLiveThresholdPercent=90 " +
      "-XX:+UseStringDeduplication -Djava.net.preferIPv4Stack=true -Dfile.encoding=UTF-8 " +
      "-Dlog4j2.formatMsgNoLookups=true -XX:+IgnoreUnrecognizedVMOptions " +
      "-XX:-OmitStackTraceInFastThrow",
  },
  {
    value: "lowmem",
    label: "低内存",
    args:
      "-XX:+UseSerialGC -XX:+DisableExplicitGC " +
      "-Djava.net.preferIPv4Stack=true -Dfile.encoding=UTF-8 " +
      "-Dlog4j2.formatMsgNoLookups=true -XX:+IgnoreUnrecognizedVMOptions " +
      "-XX:-OmitStackTraceInFastThrow",
  },
  {
    value: "perf",
    label: "高性能",
    args:
      "-XX:+UseShenandoahGC -XX:+AlwaysPreTouch -XX:+ParallelRefProcEnabled " +
      "-XX:+DisableExplicitGC -XX:+UseStringDeduplication " +
      "-Djava.net.preferIPv4Stack=true -Dfile.encoding=UTF-8 " +
      "-Dlog4j2.formatMsgNoLookups=true -XX:+IgnoreUnrecognizedVMOptions " +
      "-XX:-OmitStackTraceInFastThrow",
  },
  {
    value: "custom",
    label: "自定义",
    args: "",
  },
];

export function VersionSettingsDrawer({ open, versionId, onClose }: Props) {
  const [form, setForm] = useState<VersionSettings>(EMPTY);
  const [jvmArgs, setJvmArgs] = useState("");
  const [jvmPreset, setJvmPreset] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [saving, setSaving] = useState(false);
  const { notice, show, clear } = useNotice();
  const [java, setJava] = useState<JavaInfo | null | undefined>(undefined);
  const [confirmReset, setConfirmReset] = useState(false);
  /** 关闭「自定义内存」前的设置值（重新开启时恢复） */
  const memBackupRef = useRef<{ min: number; max: number } | null>(null);
  /** 可用 Java 运行时（启动器已下载 + 系统检测） */
  const [runtimes, setRuntimes] = useState<JavaRuntime[]>([]);

  // 打开时加载版本设置
  useEffect(() => {
    if (!open || !versionId) return;
    clear();
    setLoaded(false);
    getVersionSettings(versionId)
      .then((s) => {
        setForm(s);
        setJvmArgs(s.jvm_args.join(" "));
        setLoaded(true);
      })
      .catch((e) => {
        show({ text: friendlyError(e), kind: "err", raw: String(e) });
        setLoaded(true);
      });
    // 打开时自动检测一次 Java（仅展示检测结果，不覆盖版本级 Java 设置；否则「自动检测」按钮会因初始状态永久禁用）
    handleCheckJava(false);
    listJavaRuntimes()
      .then(setRuntimes)
      .catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, versionId]);

  if (!open) return null;

  const set = <K extends keyof VersionSettings>(key: K, value: VersionSettings[K]) =>
    setForm((f) => ({ ...f, [key]: value }));

  /** 版本级 Java 选择框当前值：auto / 已装路径 / custom */
  const javaSelectValue = () => {
    const v = form.java_executable ?? "";
    if (!v) return "auto";
    return runtimes.some((r) => r.executable === v) ? v : "custom";
  };

  // 内存是否启用版本级自定义（任一字段非空即视为自定义）
  const memCustom = form.min_memory_mb !== null || form.max_memory_mb !== null;
  const curMin = form.min_memory_mb ?? MEM_DEFAULT_MIN;
  const curMax = form.max_memory_mb ?? MEM_DEFAULT_MAX;

  /** 关闭时暂存用户设置值，重新开启时恢复（无暂存则用缺省值） */
  const toggleMemCustom = (on: boolean) => {
    if (on) {
      const backup = memBackupRef.current;
      setForm((f) => ({
        ...f,
        min_memory_mb: backup?.min ?? MEM_DEFAULT_MIN,
        max_memory_mb: backup?.max ?? MEM_DEFAULT_MAX,
      }));
    } else {
      if (form.min_memory_mb !== null || form.max_memory_mb !== null) {
        memBackupRef.current = {
          min: form.min_memory_mb ?? MEM_DEFAULT_MIN,
          max: form.max_memory_mb ?? MEM_DEFAULT_MAX,
        };
      }
      setForm((f) => ({ ...f, min_memory_mb: null, max_memory_mb: null }));
    }
  };

  const handleSave = async () => {
    clear();
    setSaving(true);
    let min = form.min_memory_mb;
    const max = form.max_memory_mb;
    // 最小内存不可超过最大内存
    if (min !== null && max !== null && min > max) min = max;
    try {
      await setVersionSettings(versionId, {
        ...form,
        min_memory_mb: min,
        max_memory_mb: max,
        jvm_args: jvmArgs.split(/\s+/).filter((s) => s.length > 0),
      });
      show({ text: "已保存", kind: "ok" });
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    } finally {
      setSaving(false);
    }
  };

  const doReset = async () => {
    setConfirmReset(false);
    clear();
    setSaving(true);
    try {
      await setVersionSettings(versionId, EMPTY);
      setForm(EMPTY);
      setJvmArgs("");
      memBackupRef.current = null;
      show({ text: "已恢复为全局默认设置", kind: "ok" });
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    } finally {
      setSaving(false);
    }
  };

  const handleCheckJava = async (fill = true) => {
    setJava(undefined);
    try {
      const info = await checkJava();
      setJava(info);
      if (info && fill) set("java_executable", info.executable);
    } catch (e) {
      setJava(null);
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    }
  };

  return (
    <>
      <Drawer open={open} onClose={onClose} title={loaded ? `${versionId} 设置` : "版本设置"}>
        {!loaded ? (
          <div class={styles.loading}>
            <Spinner size="sm" />
            正在加载设置…
          </div>
        ) : (
          <>
            <div class={styles.tip}>未设置的项将跟随全局默认，改动需保存后生效</div>
            <MaybeNotice notice={notice} onClose={clear} className={styles.notice} />

            {/* 内存 */}
            <div class={styles.innerCard}>
              <div class={styles.switchRow}>
                <div>
                  <div class={styles.label}>自定义内存</div>
                  <div class={styles.sub}>关闭时使用全局内存设置</div>
                </div>
                <Switch checked={memCustom} onChange={toggleMemCustom} aria-label="自定义内存" />
              </div>
              {memCustom && (
                <div class={styles.memRow}>
                  <div class={styles.sliderField}>
                    <div class={styles.fieldHead}>
                      <span class={styles.fieldLabel}>最小内存</span>
                      <span class={styles.memValue}>{fmtMemory(curMin)}</span>
                    </div>
                    <Slider
                      value={curMin}
                      min={MEM_MIN}
                      max={MEM_MAX}
                      step={MEM_STEP}
                      onChange={(v) => set("min_memory_mb", Math.min(v, curMax))}
                      ariaLabel="最小内存"
                    />
                  </div>
                  <div class={styles.sliderField}>
                    <div class={styles.fieldHead}>
                      <span class={styles.fieldLabel}>最大内存</span>
                      <span class={styles.memValue}>{fmtMemory(curMax)}</span>
                    </div>
                    <Slider
                      value={curMax}
                      min={MEM_MIN}
                      max={MEM_MAX}
                      step={MEM_STEP}
                      onChange={(v) => set("max_memory_mb", Math.max(v, curMin))}
                      ariaLabel="最大内存"
                    />
                  </div>
                </div>
              )}
            </div>

            {/* 语言 */}
            <div class={styles.innerCard}>
              <div class={styles.field}>
                <span class={styles.fieldLabel}>游戏语言</span>
                <Select
                  value={form.language ?? ""}
                  options={[
                    { value: "", label: "跟随全局" },
                    { value: "zh_CN", label: "简体中文" },
                    { value: "en_US", label: "English" },
                  ]}
                  onChange={(v) => set("language", v || null)}
                  placeholder="跟随全局"
                  title="游戏语言"
                  ariaLabel="游戏语言"
                />
              </div>
            </div>

            {/* 版本隔离 */}
            <div class={styles.innerCard}>
              <div class={styles.switchRow}>
                <div>
                  <div class={styles.label}>版本隔离</div>
                  <div class={styles.sub}>独立游戏目录（存档/资源仍共享 .minecraft）</div>
                </div>
                <Switch
                  checked={form.isolated}
                  onChange={(on) => set("isolated", on)}
                  aria-label="版本隔离"
                />
              </div>
            </div>

            {/* Java */}
            <div class={styles.innerCard}>
              <div class={`${styles.label} ${styles.labelGap}`}>Java（多版本）</div>
              <div class={`${styles.sub} ${styles.subGap}`}>
                跟随全局时按版本要求自动匹配已装/系统 Java
              </div>
              <Select
                value={javaSelectValue()}
                options={[
                  { value: "auto", label: "跟随全局（自动匹配）" },
                  ...runtimes.map((r) => ({
                    value: r.executable,
                    label: `Java ${r.major_version} · ${r.version_string}（${r.is_64bit ? "64 位" : "32 位"}${r.managed ? " · 启动器" : ""}）`,
                  })),
                  { value: "custom", label: "自定义路径…" },
                ]}
                onChange={(v) => {
                  if (v === "auto") set("java_executable", null);
                  else if (v !== "custom") set("java_executable", v);
                }}
                ariaLabel="Java 运行时"
              />
              {javaSelectValue() === "custom" && (
                <div class={styles.inlineRow}>
                  <Input
                    className={styles.grow}
                    placeholder="Java 可执行文件路径"
                    value={form.java_executable ?? ""}
                    onInput={(e) =>
                      set("java_executable", (e.target as HTMLInputElement).value || null)
                    }
                    aria-label="Java 可执行文件路径"
                  />
                  <Button variant="standard" size="sm" onClick={() => handleCheckJava()}>
                    自动检测
                  </Button>
                </div>
              )}
              {java !== undefined && (
                <div class={`${styles.hint} ${styles.hintGap}`}>
                  {java
                    ? `检测到 ${java.version_string}（Java ${java.major_version} · ${java.is_64bit ? "64 位" : "32 位"}）`
                    : "未检测到系统 Java"}
                </div>
              )}
            </div>

            {/* JVM 参数（高级设置） */}
            <div class={styles.innerCard}>
              <div class={styles.field}>
                <span class={styles.fieldLabel}>JVM 优化预设</span>
                <Select
                  value={jvmPreset}
                  options={JVM_PRESETS.map((p) => ({ value: p.value, label: p.label }))}
                  onChange={(v) => {
                    setJvmPreset(v);
                    const preset = JVM_PRESETS.find((p) => p.value === v);
                    if (preset && preset.args) setJvmArgs(preset.args);
                  }}
                  placeholder="选择预设"
                  title="JVM 优化预设"
                  ariaLabel="JVM 优化预设"
                />
                <div class={styles.hint}>
                  内置优化：G1GC 平衡 · 低内存：SerialGC 省内存 · 高性能：Shenandoah 低延迟
                </div>
              </div>
              <div class={styles.field}>
                <span class={styles.fieldLabel}>JVM 参数（高级）</span>
                <textarea
                  class={styles.textarea}
                  rows={4}
                  placeholder="留空跟随全局；例如：-XX:+UseG1GC -Xss4M"
                  value={jvmArgs}
                  onInput={(e) => {
                    setJvmArgs((e.target as HTMLTextAreaElement).value);
                    setJvmPreset("");
                  }}
                />
                <div class={styles.hint}>以空格分隔，将覆盖全局 JVM 参数</div>
              </div>
              <div class={styles.field}>
                <span class={styles.fieldLabel}>内置优化参数</span>
                <Select
                  value={
                    form.use_builtin_jvm_args === null
                      ? "global"
                      : form.use_builtin_jvm_args
                        ? "on"
                        : "off"
                  }
                  options={[
                    { value: "global", label: "跟随全局" },
                    { value: "on", label: "启用 — G1GC / 内存预触 / IPv4 优先" },
                    { value: "off", label: "关闭 — 仅使用版本指定参数" },
                  ]}
                  onChange={(v) => {
                    if (v === "global") set("use_builtin_jvm_args", null);
                    else if (v === "on") set("use_builtin_jvm_args", true);
                    else set("use_builtin_jvm_args", false);
                  }}
                  placeholder="跟随全局"
                  title="内置优化参数"
                  ariaLabel="内置优化参数"
                />
              </div>
            </div>

            <div class={styles.actions}>
              <Button
                variant="primary"
                className={styles.grow}
                onClick={handleSave}
                busy={saving}
              >
                保存设置
              </Button>
              <Button variant="standard" onClick={() => setConfirmReset(true)} disabled={saving}>
                恢复默认
              </Button>
            </div>
          </>
        )}
      </Drawer>
      <ConfirmDialog
        open={confirmReset}
        title="恢复默认设置"
        message={`将「${versionId}」恢复为全局默认设置吗？`}
        confirmLabel="恢复默认"
        onConfirm={doReset}
        onCancel={() => setConfirmReset(false)}
      />
    </>
  );
}
