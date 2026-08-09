// 账户管理：列表 / 添加离线账户 / 微软设备码登录（展示 code + 轮询）/ 删除 / 默认切换

import { useEffect, useState } from "preact/hooks";
import {
  addOfflineAccount,
  listAccounts,
  loginMicrosoftDevice,
  pollMicrosoftLogin,
  removeAccount,
  setDefaultAccount,
} from "../api";
import type { DeviceCode, StoredAccount } from "../types";

export function Accounts() {
  const [accounts, setAccounts] = useState<StoredAccount[]>([]);
  const [name, setName] = useState("");
  const [msg, setMsg] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);

  // 微软登录状态
  const [device, setDevice] = useState<DeviceCode | null>(null);
  const [loginState, setLoginState] = useState<"idle" | "fetching" | "waiting" | "done">("idle");
  const [loginMsg, setLoginMsg] = useState<string | null>(null);

  const refresh = async () => {
    setAccounts(await listAccounts().catch(() => []));
  };

  useEffect(() => {
    refresh();
  }, []);

  // 设备码轮询：按 interval 间隔循环，直至 success / error
  useEffect(() => {
    if (!device || loginState !== "waiting") return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;

    const tick = async () => {
      if (cancelled) return;
      try {
        const poll = await pollMicrosoftLogin(device.device_code);
        if (cancelled) return;
        if (poll.state === "success") {
          setLoginState("done");
          setLoginMsg(`登录成功：${poll.account?.username ?? ""}`);
          setDevice(null);
          await refresh();
        } else if (poll.state === "error") {
          setLoginState("done");
          setLoginMsg(`登录失败：${poll.message}`);
          setDevice(null);
        } else {
          setLoginMsg(poll.message);
          timer = setTimeout(tick, Math.max(device.interval, 1) * 1000);
        }
      } catch (e) {
        if (cancelled) return;
        setLoginState("done");
        setLoginMsg(`轮询出错：${e}`);
        setDevice(null);
      }
    };
    timer = setTimeout(tick, device.interval * 1000);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [device, loginState]);

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

  const handleMicrosoft = async () => {
    setErr(null);
    setMsg(null);
    setLoginState("fetching");
    setLoginMsg(null);
    setDevice(null);
    try {
      const dc = await loginMicrosoftDevice();
      setDevice(dc);
      setLoginState("waiting");
      setLoginMsg("等待授权…");
    } catch (e) {
      setLoginState("done");
      setLoginMsg(`获取设备码失败：${e}`);
    }
  };

  const cancelMicrosoft = () => {
    setDevice(null);
    setLoginState("idle");
    setLoginMsg(null);
  };

  const copyCode = async () => {
    if (!device) return;
    try {
      await navigator.clipboard.writeText(device.user_code);
      setMsg("已复制授权码");
    } catch {
      setMsg("复制失败，请手动输入");
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
      <div class="page-desc">添加离线账户或通过微软账号登录，可切换默认账户用于启动游戏</div>

      {msg && <div class="notice ok">{msg}</div>}
      {err && <div class="notice err">{err}</div>}

      <div class="card" style="margin-bottom:24px">
        <div class="section-title">离线账户</div>
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

      <div class="card" style="margin-bottom:24px">
        <div class="section-title">微软登录</div>
        {loginState === "idle" && (
          <div class="hint" style="margin-bottom:10px">
            正版登录需要浏览器打开微软授权页并输入设备码
          </div>
        )}

        {loginState === "fetching" && <div class="hint">正在获取设备码…</div>}

        {device && loginState === "waiting" && (
          <div class="ms-login">
            <div class="ms-code" onClick={copyCode} title="点击复制">
              {device.user_code}
            </div>
            <div class="hint">1. 在浏览器打开微软授权页</div>
            <div class="ms-uri">
              <a href={device.verification_uri} target="_blank" rel="noreferrer">
                {device.verification_uri}
              </a>
              <button class="btn" onClick={() => window.open(device.verification_uri, "_blank")}>
                打开
              </button>
            </div>
            <div class="hint">2. 输入上方授权码并登录（{Math.ceil(device.expires_in / 60)} 分钟内有效）</div>
            <div class="hint">3. 本页将自动轮询登录状态</div>
            <div class="ms-status">{loginMsg}</div>
            <button class="btn" onClick={cancelMicrosoft}>取消登录</button>
          </div>
        )}

        {loginState === "done" && (
          <div class="hint" style="margin-bottom:10px">{loginMsg}</div>
        )}

        {(loginState === "idle" || loginState === "done") && (
          <button class="btn" onClick={handleMicrosoft}>开始微软登录</button>
        )}
      </div>

      <div class="list">
        {accounts.length === 0 && (
          <div class="empty">暂无账户，先添加一个离线账户或完成微软登录</div>
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
