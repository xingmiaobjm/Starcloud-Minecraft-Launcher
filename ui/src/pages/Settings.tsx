// 设置：读取 / 保存启动器配置

import { useEffect, useState } from "preact/hooks";
import { checkJava, getSettings, setSettings } from "../api";
import type { JavaInfo, Settings as SettingsData } from "../types";

const EMPTY: SettingsData = {
  dot_minecraft: "",
  language: "zh-CN",
  default_min_memory_mb: 1024,
  default_max_memory_mb: 4096,
  default_window_width: 854,
  default_window_height: 480,
  java_executable: null,
  extra_jvm_args: [],
  download_concurrency: 4,
  download_max_retries: 3,
};

export function Settings() {
  const [form, setForm] = useState<SettingsData>(EMPTY);
  const [loaded, setLoaded] = useState(false);
  const [jvmArgs, setJvmArgs] = useState("");
  const [saved, setSaved] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [java, setJava] = useState<JavaInfo | null | undefined>(undefined);

  useEffect(() => {
    getSettings()
      .then((s) => {
        setForm(s);
        setJvmArgs(s.extra_jvm_args.join(" "));
        setLoaded(true);
      })
      .catch((e) => setErr(String(e)));
  }, []);

  const set = <K extends keyof SettingsData>(key: K, value: SettingsData[K]) =>
    setForm((f) => ({ ...f, [key]: value }));

  const handleSave = async () => {
    setErr(null);
    setSaved(false);
    const args = jvmArgs.split(/\s+/).filter((s) => s.length > 0);
    try {
      await setSettings({ ...form, extra_jvm_args: args });
      setSaved(true);
      setTimeout(() => setSaved(false), 3000);
    } catch (e) {
      setErr(String(e));
    }
  };

  const handleCheckJava = async () => {
    setJava(undefined);
    try {
      const info = await checkJava();
      setJava(info);
      if (info) set("java_executable", info.executable);
    } catch (e) {
      setJava(null);
      setErr(`Java 检测失败：${e}`);
    }
  };

  if (!loaded) {
    return <div class="page-title">设置</div>;
  }

  return (
    <div>
      <div class="page-title">设置</div>
      <div class="page-desc">配置游戏目录、内存、窗口与下载参数</div>

      {saved && <div class="notice ok">已保存</div>}
      {err && <div class="notice err">{err}</div>}

      <div class="card" style="margin-bottom:18px">
        <div class="section-title">基本</div>
        <div class="field">
          <label>.minecraft 目录</label>
          <input value={form.dot_minecraft} onInput={(e) => set("dot_minecraft", (e.target as HTMLInputElement).value)} />
        </div>
        <div class="field">
          <label>Java 可执行文件</label>
          <div style="display:flex;gap:10px;align-items:center">
            <input
              style="flex:1"
              value={form.java_executable ?? ""}
              placeholder="留空则自动检测"
              onInput={(e) => set("java_executable", (e.target as HTMLInputElement).value || null)}
            />
            <button class="btn" onClick={handleCheckJava} disabled={java === undefined}>
              检测 Java
            </button>
          </div>
          {java === undefined ? (
            <div class="hint">检测中…</div>
          ) : java ? (
            <div class="hint">
              {java.version_string}（Java {java.major_version} · {java.is_64bit ? "64 位" : "32 位"}）
            </div>
          ) : null}
        </div>
      </div>

      <div class="card" style="margin-bottom:18px">
        <div class="section-title">内存（MB）</div>
        <div style="display:flex;gap:20px;flex-wrap:wrap">
          <div class="field" style="flex:1;min-width:180px">
            <label>最小内存</label>
            <input
              type="number"
              min="512"
              step="256"
              value={form.default_min_memory_mb}
              onInput={(e) => set("default_min_memory_mb", Number((e.target as HTMLInputElement).value))}
            />
          </div>
          <div class="field" style="flex:1;min-width:180px">
            <label>最大内存</label>
            <input
              type="number"
              min="512"
              step="256"
              value={form.default_max_memory_mb}
              onInput={(e) => set("default_max_memory_mb", Number((e.target as HTMLInputElement).value))}
            />
          </div>
        </div>
      </div>

      <div class="card" style="margin-bottom:18px">
        <div class="section-title">窗口</div>
        <div style="display:flex;gap:20px;flex-wrap:wrap">
          <div class="field" style="flex:1;min-width:150px">
            <label>宽</label>
            <input
              type="number"
              min="640"
              step="16"
              value={form.default_window_width}
              onInput={(e) => set("default_window_width", Number((e.target as HTMLInputElement).value))}
            />
          </div>
          <div class="field" style="flex:1;min-width:150px">
            <label>高</label>
            <input
              type="number"
              min="480"
              step="16"
              value={form.default_window_height}
              onInput={(e) => set("default_window_height", Number((e.target as HTMLInputElement).value))}
            />
          </div>
        </div>
      </div>

      <div class="card" style="margin-bottom:18px">
        <div class="section-title">下载</div>
        <div style="display:flex;gap:20px;flex-wrap:wrap">
          <div class="field" style="flex:1;min-width:150px">
            <label>并发数</label>
            <input
              type="number"
              min="1"
              max="16"
              value={form.download_concurrency}
              onInput={(e) => set("download_concurrency", Number((e.target as HTMLInputElement).value))}
            />
          </div>
          <div class="field" style="flex:1;min-width:150px">
            <label>最大重试</label>
            <input
              type="number"
              min="1"
              max="10"
              value={form.download_max_retries}
              onInput={(e) => set("download_max_retries", Number((e.target as HTMLInputElement).value))}
            />
          </div>
        </div>
      </div>

      <div class="card" style="margin-bottom:18px">
        <div class="section-title">JVM 附加参数</div>
        <div class="field">
          <label>参数（空格分隔）</label>
          <input
            value={jvmArgs}
            placeholder="例如 -Dfml.ignoreInvalidMinecraftCertificates=true"
            onInput={(e) => setJvmArgs((e.target as HTMLInputElement).value)}
          />
          <div class="hint">内存参数已由上方配置自动生成，无需重复填写</div>
        </div>
      </div>

      <button class="btn-primary" onClick={handleSave}>保存设置</button>
    </div>
  );
}
