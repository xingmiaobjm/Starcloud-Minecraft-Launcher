// 首页：右侧 3:4 启动卡片（版本按钮菜单 / 正版·离线滑块 / 进度内嵌）

import { useEffect, useState } from "preact/hooks";
import LiquidGlass from "liquid-glass-react";
import { launchGame, listInstalled, listVersions, stopGame } from "../api";
import { VersionMenu } from "../components/VersionMenu";
import type { AppStatus, ProgressPayload, VersionInfo } from "../types";

interface Props {
  status: AppStatus;
  statusDetail: string | null;
  progress: ProgressPayload | null;
  lastError: string | null;
  onClearError: () => void;
}

export function Home({ status, statusDetail, progress, lastError, onClearError }: Props) {
  const [versions, setVersions] = useState<VersionInfo[]>([]);
  const [selected, setSelected] = useState("");
  const [username, setUsername] = useState("");
  const [mode, setMode] = useState<"offline" | "microsoft">("offline");
  const [loading, setLoading] = useState(true);
  const [localErr, setLocalErr] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try {
        const vs = await listVersions();
        setVersions(vs);
        const installed = vs.find((v) => v.installed);
        setSelected(installed?.id ?? vs[0]?.id ?? "");
      } catch {
        // 网络失败时退化为本地已安装列表
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
          setSelected(inst[0]?.id ?? "");
        } catch {
          /* ignore */
        }
      }
      setLoading(false);
    })();
  }, [status === "idle"]); // 状态回闲时刷新安装状态

  const busy = status === "installing" || status === "launching" || status === "running";
  const installed = versions.find((v) => v.id === selected)?.installed ?? false;
  const selectedMeta = versions.find((v) => v.id === selected);

  const handleLaunch = async () => {
    if (!selected || (mode === "offline" && !username.trim())) return;
    onClearError();
    setLocalErr(null);
    try {
      await launchGame(selected, username.trim());
    } catch (e) {
      onClearError();
      setLocalErr(String(e));
    }
  };

  const handleStop = async () => {
    setLocalErr(null);
    try {
      await stopGame();
    } catch (e) {
      setLocalErr(String(e));
    }
  };

  return (
    <div class="home">
      <LiquidGlass
        className="launch-panel launch-panel-lg"
        cornerRadius={14}
        padding="24px 22px"
        blurAmount={0.22}
        saturation={150}
        displacementScale={70}
        aberrationIntensity={2}
        elasticity={0.12}
      >
        <div class="mode-slider">
          <div class={`mode-thumb ${mode === "offline" ? "offline" : ""}`} />
          <button
            class={`mode-opt ${mode === "microsoft" ? "active" : ""}`}
            onClick={() => setMode("microsoft")}
          >
            正版
          </button>
          <button
            class={`mode-opt ${mode === "offline" ? "active" : ""}`}
            onClick={() => setMode("offline")}
          >
            离线
          </button>
        </div>

        <div class="panel-title">启动游戏</div>

        <div class="field">
          <label>版本</label>
          <VersionMenu versions={versions} selected={selected} loading={loading} onSelect={setSelected} />
          {selectedMeta && selectedMeta.version_type && (
            <div class="hint" style="margin-top:6px">
              {selectedMeta.version_type} · {selectedMeta.release_time || "官方版本"}
            </div>
          )}
        </div>

        {mode === "offline" ? (
          <div class="field">
            <label>玩家名</label>
            <input
              value={username}
              maxLength={16}
              placeholder="输入玩家名"
              onInput={(e) => setUsername((e.target as HTMLInputElement).value)}
            />
          </div>
        ) : (
          <div class="field">
            <label>微软账户</label>
            <div class="hint">正版登录即将推出，当前请使用离线模式</div>
          </div>
        )}

        {status === "running" ? (
          <button class="btn-danger launch-btn" onClick={handleStop} disabled={!busy}>
            停止游戏
          </button>
        ) : (
          <button
            class="btn-primary launch-btn"
            onClick={handleLaunch}
            disabled={busy || !selected || (mode === "offline" && !username.trim())}
          >
            {status === "launching"
              ? "启动中…"
              : !installed
                ? "先安装再启动"
                : mode === "offline"
                  ? "启动游戏"
                  : "正版登录暂不可用"}
          </button>
        )}

        {progress && status === "installing" && (
          <div class="progress-wrap">
            <div class="progress-head">
              <span>{progress.stage || "下载中…"}</span>
              <span class="pct">{progress.percentage.toFixed(1)}%</span>
            </div>
            <div class="progress-bar">
              <div class="progress-fill" style={{ width: `${progress.percentage}%` }} />
            </div>
          </div>
        )}

        {statusDetail && status !== "installing" && status !== "launching" && (
          <div class="status-detail">{statusDetail}</div>
        )}

        {(lastError || localErr) && (
          <div class="error-box">
            {lastError && (
              <button class="dismiss" onClick={onClearError} aria-label="关闭">
                ×
              </button>
            )}
            {localErr ? localErr : lastError}
          </div>
        )}
      </LiquidGlass>
    </div>
  );
}
