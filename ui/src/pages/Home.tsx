// 首页 —— PCL2 式左卡布局（Fluent Native 材质）
// 左列：账户模式 → 头像/账户 → 版本选择 → 启动主按钮 → 次要操作
// 右列：问候语 + 提示 + 新手引导 + 快捷入口

import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { Check, ChevronRight, Download as DownloadIcon, Play, Puzzle, Square, Wifi } from "lucide";
import { statusDotPulse } from "../motion";
import { friendlyError } from "../errorMessages";
import {
  getSettings,
  launchGame,
  listAccounts,
  listInstalled,
  listVersions,
  preflightCheck,
  setDefaultAccount,
  stopGame,
} from "../api";
import { Button } from "../components/Button/Button";
import { Card } from "../components/Card/Card";
import { Icon } from "../components/Icon/Icon";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Input } from "../components/Input/Input";
import { Notice } from "../components/Notice/Notice";
import { ProgressBar } from "../components/ProgressBar/ProgressBar";
import { Select } from "../components/Select/Select";
import { Spinner } from "../components/Spinner/Spinner";
import { Tag } from "../components/Tag/Tag";
import { VersionMenu } from "../components/VersionMenu";
import { VersionSettingsDrawer } from "../components/VersionSettingsDrawer";
import { useNotice } from "../hooks/useNotice";
import { fmtBytes } from "../utils/format";
import type { AppStatus, ProgressPayload, StoredAccount, VersionInfo } from "../types";
import styles from "./Home.module.css";

/** 「启动游戏」引导步骤的完成标记（跨会话持久化） */
const LAUNCHED_KEY = "sc.launch.launched";

interface Props {
  status: AppStatus;
  statusDetail: string | null;
  progress: ProgressPayload | null;
  lastError: string | null;
  onClearError: () => void;
  onGoDownload: () => void;
  onGoAccounts: () => void;
  onGoMods: () => void;
  onGoTerracotta: () => void;
}

/** 按当前时刻返回问候语 */
function greeting(): string {
  const h = new Date().getHours();
  if (h < 6) return "夜深了";
  if (h < 12) return "早上好";
  if (h < 18) return "下午好";
  return "晚上好";
}

export function Home({
  status,
  statusDetail,
  progress,
  lastError,
  onClearError,
  onGoDownload,
  onGoAccounts,
  onGoMods,
  onGoTerracotta,
}: Props) {
  // ── 版本 ──
  const [versions, setVersions] = useState<VersionInfo[]>([]);
  const [versionsLoading, setVersionsLoading] = useState(true);
  const [versionsError, setVersionsError] = useState(false);
  const [selected, setSelected] = useState("");
  const [menuOpen, setMenuOpen] = useState(false);
  const [settingsVersion, setSettingsVersion] = useState<string | null>(null);
  const [reloadTick, setReloadTick] = useState(0);

  // ── 账户 ──
  const [accounts, setAccounts] = useState<StoredAccount[]>([]);
  const [accountsError, setAccountsError] = useState(false);
  const [selectedAccount, setSelectedAccount] = useState("");
  const [username, setUsername] = useState("");
  const [mode, setMode] = useState<"offline" | "microsoft">("offline");

  // ── 提示（preflight 错误 / 警告）与启动引导状态 ──
  const { notice: localNotice, show: showLocal, clear: clearLocal } = useNotice();
  const { notice: warnNotice, show: showWarn, clear: clearWarn } = useNotice();
  const [launchedOnce, setLaunchedOnce] = useState(() => {
    try {
      return localStorage.getItem(LAUNCHED_KEY) === "1";
    } catch {
      return false;
    }
  });

  const launchBtnRef = useRef<HTMLButtonElement>(null);
  const runDotRef = useRef<HTMLSpanElement>(null);

  // ── 数据加载：失败不再静默吞掉，分别记录错误态供重试 ──
  const loadVersions = useCallback(async () => {
    setVersionsLoading(true);
    setVersionsError(false);
    try {
      const vs = await listVersions();
      setVersions(vs);
      let last: string | null = null;
      try {
        const settings = await getSettings();
        last = settings.last_version ?? null;
      } catch {
        /* 设置读取失败不影响版本列表展示 */
      }
      const preferred =
        vs.find((v) => v.id === last && v.installed) ??
        vs.find((v) => v.installed) ??
        vs[0];
      setSelected((cur) => (cur && vs.some((v) => v.id === cur) ? cur : preferred?.id ?? ""));
    } catch {
      // 远程清单不可用时回退本地已安装列表；两者都失败才是真正的加载失败
      try {
        const inst = await listInstalled();
        setVersions(
          inst.map((v) => ({
            id: v.id,
            version_type: "本地",
            release_time: "",
            installed: true,
            size_bytes: v.size_bytes,
          })),
        );
        setSelected((cur) => (cur && inst.some((v) => v.id === cur) ? cur : inst[0]?.id ?? ""));
      } catch {
        setVersions([]);
        setSelected("");
        setVersionsError(true);
      }
    } finally {
      setVersionsLoading(false);
    }
  }, []);

  const loadAccounts = useCallback(async () => {
    setAccountsError(false);
    try {
      const accs = await listAccounts();
      setAccounts(accs);
      const def = accs.find((a) => a.is_default) ?? accs[0];
      if (def) {
        setSelectedAccount(def.uuid);
        setUsername(def.username);
      }
    } catch {
      setAccounts([]);
      setAccountsError(true);
    }
  }, []);

  // 首次进入加载
  useEffect(() => {
    void loadVersions();
  }, [loadVersions]);

  useEffect(() => {
    void loadAccounts();
  }, [loadAccounts]);

  // 从非空闲回到空闲（安装完成 / 游戏退出）时刷新版本安装状态
  const prevStatusRef = useRef(status);
  useEffect(() => {
    const wasIdle = prevStatusRef.current === "idle";
    prevStatusRef.current = status;
    if (!wasIdle && status === "idle") void loadVersions();
  }, [status, loadVersions]);

  // VersionMenu 内删除/变更版本后刷新（跳过初始 0，避免与首载重复请求）
  useEffect(() => {
    if (reloadTick > 0) void loadVersions();
  }, [reloadTick, loadVersions]);

  // 游戏成功运行后持久化「启动游戏」引导步骤的完成标记
  useEffect(() => {
    if (status !== "running") return;
    setLaunchedOnce(true);
    try {
      localStorage.setItem(LAUNCHED_KEY, "1");
    } catch {
      /* 忽略存储失败 */
    }
  }, [status]);

  // 运行状态点脉冲动画
  useEffect(() => {
    if (status !== "running") return;
    const dot = runDotRef.current;
    if (!dot) return;
    const mm = statusDotPulse(dot, "15,123,15");
    return () => mm.revert();
  }, [status]);

  // ── 派生状态 ──
  const offlineAccounts = accounts.filter((a) => a.type === "offline");
  const microsoftAccounts = accounts.filter((a) => a.type === "microsoft");
  const modeAccounts = mode === "offline" ? offlineAccounts : microsoftAccounts;
  // 选中账户限定在当前模式内，避免跨模式误用
  const modeSelected =
    modeAccounts.some((a) => a.uuid === selectedAccount)
      ? selectedAccount
      : modeAccounts.find((a) => a.is_default)?.uuid ?? modeAccounts[0]?.uuid ?? "";
  const currentAccount = modeAccounts.find((a) => a.uuid === modeSelected) ?? null;

  const busy = status === "installing" || status === "launching" || status === "running";
  const selectedMeta = versions.find((v) => v.id === selected) ?? null;
  const installed = selectedMeta?.installed ?? false;
  const launchName = currentAccount?.username ?? username.trim();
  const needMicrosoftAccount = mode === "microsoft" && microsoftAccounts.length === 0;

  // ── 引导步骤：与真实状态联动 ──
  const hasInstalledVersion = versions.some((v) => v.installed);
  const hasAccount = accounts.length > 0 || username.trim().length > 0;
  const launchDone = launchedOnce || status === "running";
  const guideSteps = [
    {
      key: "install",
      title: "安装版本",
      desc: hasInstalledVersion ? "已安装游戏版本" : "从下载页安装一个 Minecraft 版本",
      done: hasInstalledVersion,
      onClick: () => onGoDownload(),
    },
    {
      key: "account",
      title: "配置账户",
      desc: hasAccount ? "已有可用账户" : "添加微软账户或创建离线玩家名",
      done: hasAccount,
      onClick: () => onGoAccounts(),
    },
    {
      key: "launch",
      title: "启动游戏",
      desc: launchDone ? "已成功启动过游戏" : "回到本页选择版本后点击启动",
      done: launchDone,
      onClick: () => launchBtnRef.current?.focus(),
    },
  ];
  const currentStep = guideSteps.findIndex((s) => !s.done);
  const guideVisible = !versionsLoading && !versionsError && currentStep !== -1;

  const segCls = (active: boolean) =>
    [styles.segOpt, active ? styles.segOptActive : ""].filter(Boolean).join(" ");

  const pickAccount = async (uuid: string) => {
    setSelectedAccount(uuid);
    const acc = accounts.find((a) => a.uuid === uuid);
    if (acc) setUsername(acc.username);
    clearLocal();
    try {
      await setDefaultAccount(uuid);
    } catch {
      /* 非阻塞：默认账户设置失败不影响本地选择 */
    }
  };

  const handleLaunch = async () => {
    if (!selected || (mode === "offline" && !launchName)) return;
    onClearError();
    clearLocal();
    clearWarn();
    try {
      const issues = await preflightCheck(selected);
      const errors = issues.filter((i) => i.level === "error");
      const warnings = issues.filter((i) => i.level === "warning");
      if (errors.length) {
        showLocal(
          { text: errors.map((e) => `${e.title}：${e.message}`).join("\n"), kind: "err" },
          null,
        );
        return;
      }
      if (warnings.length) {
        showWarn(
          { text: warnings.map((w) => `${w.title}：${w.message}`).join("\n"), kind: "info" },
          null,
        );
      }
      if (currentAccount) {
        try {
          await setDefaultAccount(currentAccount.uuid);
        } catch {
          /* 非阻塞 */
        }
      }
      await launchGame(selected, launchName);
    } catch (e) {
      onClearError();
      showLocal(
        {
          text: friendlyError(e),
          kind: "err",
          raw: typeof e === "string" ? e : e instanceof Error ? e.message : undefined,
        },
        null,
      );
    }
  };

  const handleStop = async () => {
    clearLocal();
    try {
      await stopGame();
    } catch (e) {
      showLocal({ text: friendlyError(e), kind: "err" }, null);
    }
  };

  // 左卡头像：当前账户名首字母（无账户时用占位符）
  const avatarChar = (currentAccount?.username ?? username.trim() ?? "").charAt(0).toUpperCase();

  // 主按钮标题与副标题（PCL 式双行）
  const launchTitle =
    status === "launching" ? "启动中…" : status === "running" ? "停止游戏" : "启动游戏";
  const launchSub = selected ? selected : "未选择版本";

  const quickTiles = [
    { key: "download", icon: DownloadIcon, title: "下载版本", desc: "获取官方版本与加载器", onClick: onGoDownload },
    { key: "mods", icon: Puzzle, title: "模组资源", desc: "搜索并安装 Mod 与整合包", onClick: onGoMods },
    { key: "terracotta", icon: Wifi, title: "好友联机", desc: "无需公网 IP，一键开黑", onClick: onGoTerracotta },
  ];

  return (
    <div class={styles.home}>
      {/* ══ 左列：PCL 式启动面板 ══ */}
      <Card className={styles.launchPanel}>
        {/* 账户模式：正版 / 离线 */}
        <div class={styles.modeSeg} role="group" aria-label="账户模式">
          <button
            type="button"
            class={segCls(mode === "microsoft")}
            aria-pressed={mode === "microsoft"}
            onClick={() => setMode("microsoft")}
          >
            正版
          </button>
          <button
            type="button"
            class={segCls(mode === "offline")}
            aria-pressed={mode === "offline"}
            onClick={() => setMode("offline")}
          >
            离线
          </button>
        </div>

        {/* 头像 + 账户区（PCL 中央位） */}
        <div class={styles.accountZone}>
          <div class={styles.avatar} aria-hidden="true">
            {avatarChar || "?"}
          </div>
          <div class={styles.accountName} role="status">
            {modeAccounts.length > 0 ? (
              currentAccount?.username ?? "未选择账户"
            ) : mode === "offline" ? (
              <Input
                value={username}
                maxLength={16}
                placeholder="输入玩家名"
                aria-label="离线玩家名"
                onInput={(e) => setUsername((e.target as HTMLInputElement).value)}
              />
            ) : needMicrosoftAccount ? (
              <span class={styles.accountHint}>尚无微软账户</span>
            ) : (
              "未选择账户"
            )}
          </div>
        </div>

        {/* 账户选择（有账户时）/ 前往添加 */}
        {accountsError ? (
          <EmptyState
            error
            title="账户列表加载失败"
            desc="无法读取已保存的账户"
            className={styles.panelEmpty}
            action={
              <Button variant="standard" onClick={() => void loadAccounts()}>
                重新加载
              </Button>
            }
          />
        ) : modeAccounts.length > 0 ? (
          <Select
            className={styles.control}
            value={modeSelected}
            onChange={pickAccount}
            ariaLabel={mode === "offline" ? "离线账户" : "微软账户"}
            options={modeAccounts.map((a) => ({
              value: a.uuid,
              label: a.is_default ? `${a.username}（默认）` : a.username,
            }))}
          />
        ) : needMicrosoftAccount ? (
          <Button variant="standard" className={styles.grow} onClick={onGoAccounts}>
            前往添加微软账户
          </Button>
        ) : null}

        {/* 版本选择 */}
        {versionsError ? (
          <EmptyState
            error
            title="版本列表加载失败"
            desc="无法获取版本信息，请检查网络后重试"
            className={styles.panelEmpty}
            action={
              <Button variant="standard" onClick={() => void loadVersions()}>
                重新加载
              </Button>
            }
          />
        ) : versionsLoading ? (
          <div class={styles.loadingRow} role="status">
            <Spinner size="sm" />
            <span>正在加载版本列表…</span>
          </div>
        ) : versions.length === 0 ? (
          <EmptyState
            title="暂无版本"
            desc="还没有任何可用的 Minecraft 版本"
            className={styles.panelEmpty}
            action={
              <Button variant="standard" onClick={onGoDownload}>
                前往下载
              </Button>
            }
          />
        ) : (
          <Select
            className={styles.control}
            value={selected}
            onChange={setSelected}
            ariaLabel="游戏版本"
            filterable
            placeholder="请选择版本"
            options={versions.map((v) => ({
              value: v.id,
              label: v.installed ? `${v.id}（已安装）` : v.id,
            }))}
          />
        )}

        {/* 版本元信息 */}
        {selectedMeta && !versionsError && (
          <div class={styles.metaLine}>
            <span>
              {selectedMeta.version_type || "未知类型"}
              {selectedMeta.release_time ? ` · ${selectedMeta.release_time}` : ""}
            </span>
            {selectedMeta.installed && selectedMeta.size_bytes > 0 && (
              <span>· {fmtBytes(selectedMeta.size_bytes)}</span>
            )}
            {selectedMeta.installed && <Tag variant="success">已安装</Tag>}
          </div>
        )}

        {/* 主操作：PCL 式双行大按钮（标题 + 版本副行） */}
        {!installed && !needMicrosoftAccount ? (
          <Button
            ref={launchBtnRef}
            size="lg"
            className={styles.launchBtn}
            onClick={onGoDownload}
            disabled={busy || !selected}
          >
            <span class={styles.btnMain}>
              <span class={styles.btnTitle}>下载版本</span>
              <span class={styles.btnSub}>{launchSub}</span>
            </span>
          </Button>
        ) : needMicrosoftAccount ? (
          <Button ref={launchBtnRef} size="lg" className={styles.launchBtn} onClick={onGoAccounts}>
            <span class={styles.btnMain}>
              <span class={styles.btnTitle}>添加微软账户</span>
              <span class={styles.btnSub}>用于正版验证</span>
            </span>
          </Button>
        ) : (
          <Button
            ref={launchBtnRef}
            variant={status === "running" ? "danger" : "primary"}
            size="lg"
            className={styles.launchBtn}
            busy={status === "launching"}
            onClick={status === "running" ? handleStop : handleLaunch}
            disabled={
              status !== "running" &&
              (busy || !selected || (mode === "offline" && !launchName))
            }
          >
            <span class={styles.btnMain}>
              <span class={styles.btnTitle}>
                {status === "running" && (
                  <Icon icon={Square} size={16} strokeWidth={1.8} />
                )}
                {!busy && (
                  <Icon icon={Play} size={16} strokeWidth={2.2} />
                )}
                {launchTitle}
              </span>
              <span class={styles.btnSub}>{launchSub}</span>
            </span>
          </Button>
        )}

        {/* 次要操作 */}
        <div class={styles.actionsRow}>
          <Button variant="standard" className={styles.grow} onClick={() => setMenuOpen(true)}>
            选择版本
          </Button>
          <Button
            variant="standard"
            className={styles.grow}
            disabled={!selected}
            onClick={() => selected && setSettingsVersion(selected)}
          >
            版本管理
          </Button>
        </div>

        {/* 启动 / 运行状态 */}
        {status === "launching" && (
          <div class={styles.statusRow} role="status">
            <Spinner size="sm" />
            <span>{statusDetail || "正在启动游戏…"}</span>
          </div>
        )}

        {status === "running" && (
          <div class={styles.statusRow} role="status">
            <span class={styles.statusDot} ref={runDotRef} aria-hidden="true" />
            <span>游戏运行中{statusDetail ? ` · ${statusDetail}` : ""}</span>
          </div>
        )}

        {/* 安装进度（speed / eta / bytes） */}
        {progress && status === "installing" && (
          <ProgressBar
            percentage={progress.percentage}
            stage={progress.stage || "下载中…"}
            speedBps={progress.speed_bytes_per_sec}
            downloadedBytes={progress.downloaded_bytes}
            totalBytes={progress.total_bytes}
            etaSecs={progress.eta_secs}
            label={`正在安装 ${selected}`}
          />
        )}

        {statusDetail && status !== "installing" && status !== "launching" && status !== "running" && (
          <div class={styles.statusDetail} role="status">
            {statusDetail}
          </div>
        )}
      </Card>

      {/* ══ 右列：问候 + 提示 + 引导 + 快捷入口 ══ */}
      <div class={styles.right}>
        <header class="page-header">
          <h1 class="page-title">{greeting()}</h1>
          <p class="page-desc">在左侧选择版本与账户，一键启动游戏</p>
        </header>

        {/* 提示区：preflight 警告 / 本地错误 / 全局错误，均可关闭、正文可复制 */}
        {warnNotice && (
          <Notice notice={warnNotice} onClose={clearWarn} className={styles.noticeMulti} />
        )}
        {localNotice && (
          <Notice notice={localNotice} onClose={clearLocal} className={styles.noticeMulti} />
        )}
        {lastError && (
          <Notice
            notice={{ text: lastError, kind: "err" }}
            onClose={onClearError}
            className={styles.noticeMulti}
          />
        )}

        {/* 首次使用引导：已完成打勾，当前步骤高亮，可点击跳转 */}
        {guideVisible && (
          <Card className={styles.guideCard}>
            <div class={styles.cardTitle}>开始使用</div>
            {guideSteps.map((step, i) => {
              const isCurrent = i === currentStep;
              return (
                <button
                  key={step.key}
                  type="button"
                  class={[
                    styles.stepRow,
                    step.done ? styles.stepRowDone : "",
                    isCurrent ? styles.stepRowCurrent : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  onClick={step.onClick}
                >
                  <span
                    class={[
                      styles.stepBadge,
                      step.done ? styles.stepBadgeDone : "",
                      isCurrent ? styles.stepBadgeCurrent : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    aria-hidden="true"
                  >
                    {step.done ? (
                      <Icon icon={Check} size={14} strokeWidth={2} />
                    ) : (
                      i + 1
                    )}
                  </span>
                  <span class={styles.stepMain}>
                    <span class={styles.stepTitle}>{step.title}</span>
                    <span class={styles.stepDesc}>{step.desc}</span>
                  </span>
                  <Icon
                    icon={ChevronRight}
                    size={16}
                    strokeWidth={1.8}
                    class={styles.stepChevron}
                  />
                </button>
              );
            })}
          </Card>
        )}

        {/* 快捷入口（Store 卡片行） */}
        <div class={styles.tiles}>
          {quickTiles.map((t) => (
            <Card key={t.key} className={styles.tile} onClick={t.onClick} ariaLabel={t.title}>
              <span class={styles.tileIcon} aria-hidden="true">
                <Icon icon={t.icon} size={20} strokeWidth={1.5} />
              </span>
              <span class={styles.tileMain}>
                <span class={styles.tileTitle}>{t.title}</span>
                <span class={styles.tileDesc}>{t.desc}</span>
              </span>
              <Icon icon={ChevronRight} size={16} strokeWidth={1.8} class={styles.tileChevron} />
            </Card>
          ))}
        </div>
      </div>

      <VersionMenu
        open={menuOpen}
        onClose={() => setMenuOpen(false)}
        versions={versions}
        selected={selected}
        loading={versionsLoading}
        onSelect={setSelected}
        onManageVersion={(id) => {
          setMenuOpen(false);
          setSettingsVersion(id);
        }}
        onVersionChanged={() => setReloadTick((t) => t + 1)}
        onGoDownload={onGoDownload}
        onSuccess={(text) => showLocal({ text, kind: "ok" })}
        onError={(text, raw) => showLocal({ text, kind: "err", raw })}
      />

      {settingsVersion && (
        <VersionSettingsDrawer
          open={Boolean(settingsVersion)}
          versionId={settingsVersion}
          onClose={() => setSettingsVersion(null)}
        />
      )}
    </div>
  );
}
