// 账户管理：列表 / 添加离线账户 / 删除 / 默认切换

import { useEffect, useState } from "preact/hooks";
import {
  addOfflineAccount,
  listAccounts,
  removeAccount,
  setDefaultAccount,
} from "../api";
import type { StoredAccount } from "../types";

export function Accounts() {
  const [accounts, setAccounts] = useState<StoredAccount[]>([]);
  const [name, setName] = useState("");
  const [msg, setMsg] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => {
    setAccounts(await listAccounts().catch(() => []));
  };

  useEffect(() => {
    refresh();
  }, []);

  const handleAdd = async () => {
    setMsg(null);
    setErr(null);
    const uname = name.trim();
    if (!uname) return;
    try {
      await addOfflineAccount(uname);
      setName("");
      await refresh();
      setMsg(`已添加离线账户 ${uname}`);
    } catch (e) {
      setErr(String(e));
    }
  };

  const handleRemove = async (a: StoredAccount) => {
    setErr(null);
    try {
      await removeAccount(a.uuid);
      await refresh();
      setMsg(`已删除 ${a.username}`);
    } catch (e) {
      setErr(String(e));
    }
  };

  const handleDefault = async (a: StoredAccount) => {
    setErr(null);
    try {
      await setDefaultAccount(a.uuid);
      await refresh();
      setMsg(`已将 ${a.username} 设为默认`);
    } catch (e) {
      setErr(String(e));
    }
  };

  return (
    <div>
      <div class="page-title">账户管理</div>
      <div class="page-desc">添加离线账户用于启动游戏，后续版本支持微软登录</div>

      {msg && <div class="notice ok">{msg}</div>}
      {err && <div class="notice err">{err}</div>}

      <div class="card" style="margin-bottom:24px">
        <div style="display:flex;gap:10px;align-items:center">
          <input
            style="flex:1"
            placeholder="输入离线账户名称…"
            value={name}
            onInput={(e) => setName((e.target as HTMLInputElement).value)}
            onKeyDown={(e) => e.key === "Enter" && handleAdd()}
          />
          <button class="btn-primary" onClick={handleAdd} disabled={!name.trim()}>
            添加离线账户
          </button>
        </div>
      </div>

      <div class="list">
        {accounts.length === 0 && (
          <div class="empty">暂无账户，先添加一个离线账户开始</div>
        )}
        {accounts.map((a) => (
          <div class="list-item" key={a.uuid}>
            <div>
              <div class="primary">
                {a.username}
                {a.is_default && <span class="tag">默认</span>}
              </div>
              <div class="secondary">
                {a.type === "offline" ? "离线账户" : a.type === "microsoft" ? "微软账户" : "Mojang 账户"}
                {a.last_used ? ` · 上次使用 ${a.last_used}` : ""}
              </div>
            </div>
            <div style="display:flex;gap:8px;align-items:center">
              {!a.is_default && (
                <button class="btn" onClick={() => handleDefault(a)}>设为默认</button>
              )}
              <button class="btn-danger" onClick={() => handleRemove(a)}>删除</button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
