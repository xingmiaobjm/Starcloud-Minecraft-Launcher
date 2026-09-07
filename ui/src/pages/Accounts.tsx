// 账户管理：列表 / 添加离线账户 / 微软设备码登录（展示 code + 轮询）/ 删除（二次确认）/ 默认切换

import { useEffect, useRef, useState } from "preact/hooks";
import { Check, Copy, UserRound } from "lucide";
import {
  addOfflineAccount,
  listAccounts,
  loginMicrosoftDevice,
  pollMicrosoftLogin,
  removeAccount,
  setDefaultAccount,
} from "../api";
import { Button } from "../components/Button/Button";
import { Card } from "../components/Card/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Icon } from "../components/Icon/Icon";
import { Input } from "../components/Input/Input";
import { MaybeNotice } from "../components/Notice/Notice";
import { Tag } from "../components/Tag/Tag";
import { friendlyError } from "../errorMessages";
import { useNotice } from "../hooks/useNotice";
import type { DeviceCode, StoredAccount } from "../types";
import styles from "./Accounts.module.css";

/** 离线角色名规则：3-16 位字母 / 数字 / 下划线 */
const NAME_RE = /^[A-Za-z0-9_]{3,16}$/;
/** 微软轮询：连续网络异常容忍次数，超过才终止流程 */
const POLL_MAX_FAILS = 3;

type LoginState = "idle" | "fetching" | "waiting" | "done" | "error";

export function Accounts() {
  const [accounts, setAccounts] = useState<StoredAccount[]>([]);
  const [loadFailed, setLoadFailed] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [nameTouched, setNameTouched] = useState(false);
  // busyKey = `${操作}:${uuid}`，只禁用正在操作的账户/按钮
  const [busyKeys, setBusyKeys] = useState<Set<string>>(new Set());
  const [pendingRemove, setPendingRemove] = useState<StoredAccount | null>(null);

  const { notice, show, clear } = useNotice();

  // 微软登录状态
  const [device, setDevice] = useState<DeviceCode | null>(null);
  const [loginState, setLoginState] = useState<LoginState>("idle");
  const [loginMsg, setLoginMsg] = useState<string | null>(null);

  const isBusy = (key: string) => busyKeys.has(key);
  const withBusy = async (key: string, fn: () => Promise<void>) => {
    setBusyKeys((s) => new Set(s).add(key));
    try {
      await fn();
    } finally {
      setBusyKeys((s) => {
        const next = new Set(s);
        next.delete(key);
        return next;
      });
    }
  };

  // 复制授权码后的短暂「已复制」反馈（Copy → Check 形变 1.6s 后回切）
  const [copied, setCopied] = useState(false);
  const copiedTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (copiedTimer.current) clearTimeout(copiedTimer.current);
    },
    [],
  );

  const refresh = async () => {
    try {
      setAccounts(await listAccounts());
      setLoadFailed(false);
      setLoadError(null);
    } catch (e) {
      setLoadFailed(true);
      setLoadError(friendlyError(e));
    }
  };

  useEffect(() => {
    refresh();
  }, []);

  // 设备码轮询：按 interval 间隔循环；网络异常连续 POLL_MAX_FAILS 次才终止（单次波动自动重试）
  useEffect(() => {
    if (!device || loginState !== "waiting") return;
    let cancelled = false;
    let failCount = 0;
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
          setLoginState("error");
          setLoginMsg(`登录失败：${poll.message}`);
          setDevice(null);
        } else {
          setLoginMsg(poll.message || "等待授权…");
          timer = setTimeout(tick, Math.max(device.interval, 1) * 1000);
        }
      } catch (e) {
        if (cancelled) return;
        failCount += 1;
        if (failCount >= POLL_MAX_FAILS) {
          setLoginState("error");
          setLoginMsg(null);
          setDevice(null);
          show({
            text: `登录状态查询连续失败，已中止本次登录：${friendlyError(e)}`,
            kind: "err",
            raw: String(e),
          });
        } else {
          setLoginMsg(`网络波动，正在重试（${failCount}/${POLL_MAX_FAILS}）…`);
          timer = setTimeout(tick, Math.max(device.interval, 1) * 1000);
        }
      }
    };
    timer = setTimeout(tick, Math.max(device.interval, 1) * 1000);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [device, loginState]);

  const nameValid = NAME_RE.test(name.trim());
  const nameError =
    nameTouched && name.trim() && !nameValid
      ? "角色名只能包含字母、数字、下划线，长度 3-16 个字符"
      : undefined;

  const handleAdd = async () => {
    const uname = name.trim();
    if (!nameValid) {
      setNameTouched(true);
      return;
    }
    clear();
    await withBusy("offline-add", async () => {
      try {
        await addOfflineAccount(uname);
        setName("");
        setNameTouched(false);
        await refresh();
        show({ text: `已添加离线账户 ${uname}`, kind: "ok" });
      } catch (e) {
        show({ text: `添加离线账户失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
      }
    });
  };

  const startMicrosoft = async () => {
    clear();
    setLoginState("fetching");
    setLoginMsg(null);
    setDevice(null);
    await withBusy("ms-login", async () => {
      try {
        const dc = await loginMicrosoftDevice();
        setDevice(dc);
        setLoginState("waiting");
        setLoginMsg("等待授权…");
      } catch (e) {
        setLoginState("done");
        setLoginMsg(null);
        show({ text: `获取设备码失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
      }
    });
  };

  const cancelMicrosoft = () => {
    clear();
    setDevice(null);
    setLoginState("idle");
    setLoginMsg(null);
  };

  const copyCode = async () => {
    if (!device) return;
    try {
      await navigator.clipboard.writeText(device.user_code);
      setCopied(true);
      if (copiedTimer.current) clearTimeout(copiedTimer.current);
      copiedTimer.current = setTimeout(() => setCopied(false), 1600);
      show({ text: "已复制", kind: "ok" });
    } catch (e) {
      show({ text: "复制失败，请手动输入授权码", kind: "err", raw: String(e) });
    }
  };

  const confirmRemove = async () => {
    if (!pendingRemove) return;
    const target = pendingRemove;
    setPendingRemove(null);
    clear();
    await withBusy(`remove:${target.uuid}`, async () => {
      try {
        await removeAccount(target.uuid);
        await refresh();
        show({ text: `已删除 ${target.username}`, kind: "ok" });
      } catch (e) {
        show({ text: `删除账户失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
      }
    });
  };

  const handleDefault = async (a: StoredAccount) => {
    clear();
    await withBusy(`default:${a.uuid}`, async () => {
      try {
        await setDefaultAccount(a.uuid);
        await refresh();
        show({ text: `已将 ${a.username} 设为默认`, kind: "ok" });
      } catch (e) {
        show({ text: `设置默认账户失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
      }
    });
  };

  const typeLabel = (t: StoredAccount["type"]) =>
    t === "offline" ? "离线" : t === "microsoft" ? "微软" : "Mojang";

  return (
    <div class="page">
      <div class="page-header">
        <h1 class="page-title">账户</h1>
        <p class="page-desc">添加离线账户或通过微软账号登录，可切换默认账户用于启动游戏</p>
      </div>

      <MaybeNotice notice={notice} onClose={clear} />

      <Card>
        <div class={styles.cardTitle}>添加离线账户</div>
        <div class={styles.addRow}>
          <Input
            className={styles.addInput}
            label="角色名"
            placeholder="输入角色名，如 Steve"
            maxLength={16}
            value={name}
            error={nameError}
            onInput={(e) => setName((e.target as HTMLInputElement).value)}
            onBlur={() => setNameTouched(true)}
            onKeyDown={(e) => e.key === "Enter" && handleAdd()}
          />
          <Button
            className={styles.addButton}
            onClick={handleAdd}
            disabled={!nameValid}
            busy={isBusy("offline-add")}
          >
            添加
          </Button>
        </div>
      </Card>

      <Card>
        <div class={styles.cardTitle}>微软登录</div>
        {loginState === "idle" && (
          <div class={styles.msIdle}>
            <div class={styles.hint}>正版登录需要浏览器打开微软授权页并输入设备码</div>
            <Button variant="standard" onClick={startMicrosoft} busy={isBusy("ms-login")}>
              开始微软登录
            </Button>
          </div>
        )}

        {loginState === "fetching" && <div class={styles.hint}>正在获取设备码…</div>}

        {device && loginState === "waiting" && (
          <div class={styles.msLogin}>
            <div class={styles.msCodeRow}>
              <button
                type="button"
                class={styles.msCode}
                onClick={copyCode}
                title="点击复制授权码"
                aria-label={`复制授权码 ${device.user_code}`}
              >
                {device.user_code}
                <Icon
                  icon={copied ? Check : Copy}
                  size={14}
                  strokeWidth={1.6}
                  label={copied ? "已复制" : "复制"}
                />
              </button>
              <span class={styles.msCodeTip}>点击复制</span>
            </div>
            <div class={styles.hint}>1. 在浏览器打开微软授权页</div>
            <div class={styles.msUri}>
              <a href={device.verification_uri} target="_blank" rel="noreferrer">
                {device.verification_uri}
              </a>
              <Button
                variant="standard"
                size="sm"
                onClick={() => window.open(device.verification_uri, "_blank")}
              >
                打开
              </Button>
            </div>
            <div class={styles.hint}>2. 输入上方授权码并登录（{Math.ceil(device.expires_in / 60)} 分钟内有效）</div>
            <div class={styles.hint}>3. 本页将自动轮询登录状态</div>
            <div class={styles.msStatus} role="status">
              {loginMsg}
            </div>
            <div class={styles.msActions}>
              <Button variant="standard" size="sm" onClick={cancelMicrosoft}>
                取消登录
              </Button>
            </div>
          </div>
        )}

        {(loginState === "done" || loginState === "error") && (
          <div class={styles.msResult}>
            {loginMsg && (
              <div class={`${styles.hint} ${loginState === "error" ? styles.msErrorText : ""} selectable`}>
                {loginMsg}
              </div>
            )}
            {loginState === "error" && (
              <div class={styles.hint}>本次登录已中止，请检查网络后重新开始</div>
            )}
            <Button variant="standard" onClick={startMicrosoft} busy={isBusy("ms-login")}>
              {loginState === "error" ? "重新开始" : "开始微软登录"}
            </Button>
          </div>
        )}
      </Card>

      <div class={styles.list}>
        {loadFailed && accounts.length === 0 ? (
          <EmptyState
            error
            title="加载失败"
            desc={loadError ?? "无法读取账户列表，请检查后重试"}
            action={
              <Button variant="standard" onClick={refresh}>
                重新加载
              </Button>
            }
          />
        ) : accounts.length === 0 ? (
          <EmptyState
            icon={<Icon icon={UserRound} size={22} strokeWidth={1.6} />}
            title="暂无账户"
            desc="先添加一个离线账户，或通过微软登录"
          />
        ) : (
          accounts.map((a) => {
            const busy = isBusy(`remove:${a.uuid}`) || isBusy(`default:${a.uuid}`);
            return (
              <Card className={styles.accountCard} key={a.uuid}>
                <div class={styles.avatar} aria-hidden="true">
                  {a.username.slice(0, 1).toUpperCase()}
                </div>
                <div class={styles.accountInfo}>
                  <div class={styles.accountName}>
                    <span>{a.username}</span>
                    {a.is_default && <span class={styles.defaultBadge}>默认</span>}
                  </div>
                  <div class={styles.accountMeta}>
                    <Tag variant={a.type === "microsoft" ? "accent" : "default"}>
                      {typeLabel(a.type)}
                    </Tag>
                    {a.last_used && <span>上次使用 {a.last_used}</span>}
                  </div>
                </div>
                <div class={styles.accountActions}>
                  {!a.is_default && (
                    <Button
                      variant="standard"
                      size="sm"
                      onClick={() => handleDefault(a)}
                      disabled={busy}
                      busy={isBusy(`default:${a.uuid}`)}
                    >
                      设为默认
                    </Button>
                  )}
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => setPendingRemove(a)}
                    disabled={busy}
                    busy={isBusy(`remove:${a.uuid}`)}
                  >
                    删除
                  </Button>
                </div>
              </Card>
            );
          })
        )}
      </div>

      <ConfirmDialog
        open={pendingRemove !== null}
        title="删除账户"
        message={`将移除账户「${pendingRemove?.username ?? ""}」的登录信息，此操作无法撤销。`}
        confirmLabel="删除"
        danger
        onConfirm={confirmRemove}
        onCancel={() => setPendingRemove(null)}
      />
    </div>
  );
}
