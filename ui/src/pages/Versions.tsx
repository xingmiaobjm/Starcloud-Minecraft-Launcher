// 版本管理：已安装列表 + 远程安装

import { useEffect, useState } from "preact/hooks";
import { installVersion, listInstalled, listVersions, removeVersion } from "../api";
import type { InstalledVersion, VersionInfo } from "../types";

export function Versions() {
  const [installed, setInstalled] = useState<InstalledVersion[]>([]);
  const [remote, setRemote] = useState<VersionInfo[]>([]);
  const [filter, setFilter] = useState("release");
  const [search, setSearch] = useState("");
  const [busyId, setBusyId] = useState<string | null>(null);
  const [msg, setMsg] = useState<{ text: string; err?: boolean } | null>(null);

  const refresh = async () => {
    setInstalled(await listInstalled().catch(() => []));
  };

  useEffect(() => {
    refresh();
    listVersions()
      .then((vs) => setRemote(vs))
      .catch(() => {});
  }, []);

  const handleInstall = async (id: string) => {
    setBusyId(id);
    setMsg({ text: `正在安装 ${id} …（进度见首页）` });
    try {
      await installVersion(id);
      await refresh();
      setMsg({ text: `${id} 安装完成` });
    } catch (e) {
      setMsg({ text: String(e), err: true });
    }
    setBusyId(null);
  };

  const handleRemove = async (id: string) => {
    try {
      await removeVersion(id);
      await refresh();
      setMsg({ text: `${id} 已删除` });
    } catch (e) {
      setMsg({ text: String(e), err: true });
    }
  };

  const shownRemote = remote.filter((v) => {
    if (filter !== "all" && v.version_type.toLowerCase() !== filter) return false;
    if (search && !v.id.toLowerCase().includes(search.toLowerCase())) return false;
    return !v.installed;
  });

  return (
    <div>
      <div class="page-title">版本管理</div>
      <div class="page-desc">管理已安装版本，安装 Mojang 官方版本</div>

      {msg && <div class={`notice ${msg.err ? "err" : "ok"}`}>{msg.text}</div>}

      <div class="page-subtitle">已安装</div>
      <div class="list" style="margin-bottom:24px">
        {installed.length === 0 && <div class="empty">暂无已安装版本</div>}
        {installed.map((v) => (
          <div class="list-item" key={v.id}>
            <div>
              <div class="primary">{v.id}</div>
              <div class="secondary">{(v.size_bytes / 1024 / 1024).toFixed(1)} MB</div>
            </div>
            <button class="btn-danger" onClick={() => handleRemove(v.id)}>删除</button>
          </div>
        ))}
      </div>

      <div class="page-subtitle">远程安装</div>
      <div class="card" style="margin-bottom:14px">
        <div class="toolbar">
          <select value={filter} onChange={(e) => setFilter((e.target as HTMLSelectElement).value)}>
            <option value="release">正式版</option>
            <option value="snapshot">快照版</option>
            <option value="old_beta">旧版 Beta</option>
            <option value="old_alpha">旧版 Alpha</option>
            <option value="all">全部</option>
          </select>
          <input
            style="flex:1"
            placeholder="搜索版本号…"
            value={search}
            onInput={(e) => setSearch((e.target as HTMLInputElement).value)}
          />
        </div>
      </div>
      <div class="list">
        {shownRemote.slice(0, 100).map((v) => (
          <div class="list-item" key={v.id}>
            <div>
              <div class="primary">{v.id}</div>
              <div class="secondary">{v.release_time}</div>
            </div>
            <button
              class="btn-primary"
              disabled={busyId !== null}
              onClick={() => handleInstall(v.id)}
            >
              {busyId === v.id ? "安装中…" : "安装"}
            </button>
          </div>
        ))}
        {shownRemote.length === 0 && <div class="empty">无匹配版本</div>}
      </div>
    </div>
  );
}
