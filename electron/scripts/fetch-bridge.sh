#!/usr/bin/env bash
# 取得 C# 桥接（Starcloud.Bridge）并发布到 ./bridge-linux-x64。
#
# 用法：
#   ./scripts/fetch-bridge.sh                     # 用 ../starcloud-minecraft-launcher
#   ./scripts/fetch-bridge.sh /path/to/main-repo  # 指定主仓库
#   STARCLOUD_CLONE=1 ./scripts/fetch-bridge.sh   # 没有本地副本时浅克隆主仓库
#
# 背景：内核（下载/安装/启动）在 C# 侧，Linux 端只做壳，不重写后端。
# 这里只发布 Linux x64 的自包含产物，供 Electron 主进程以 `dotnet` 拉起。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/bridge-linux-x64"

MAIN_REPO="${1:-${STARCLOUD_MAIN_REPO:-$ROOT/../starcloud-minecraft-launcher}}"

if [[ ! -d "$MAIN_REPO/dotnet/src/Starcloud.Bridge" ]]; then
  if [[ "${STARCLOUD_CLONE:-0}" == "1" ]]; then
    echo "主仓库不在 $MAIN_REPO，浅克隆到 $ROOT/.bridge-src"
    git clone --depth 1 \
      git@github.com:xingmiaobjm/Starcloud-Minecraft-Launcher.git "$ROOT/.bridge-src"
    MAIN_REPO="$ROOT/.bridge-src"
  else
    echo "找不到主仓库：$MAIN_REPO" >&2
    echo "请指定路径，或设置 STARCLOUD_CLONE=1 让脚本自动克隆。" >&2
    exit 1
  fi
fi

command -v dotnet >/dev/null || { echo "需要 .NET SDK 8+（未找到 dotnet）" >&2; exit 1; }

echo "从 $MAIN_REPO 发布桥接 → $OUT"
dotnet publish "$MAIN_REPO/dotnet/src/Starcloud.Bridge" \
  -c Release -r linux-x64 --self-contained false -o "$OUT"

# 只装 .NET 9/10 的机器也要能跑（csproj 里已设 RollForward=Major，这里再兜底一次）
RUNTIME_CONFIG="$OUT/Starcloud.Bridge.runtimeconfig.json"
if [[ -f "$RUNTIME_CONFIG" ]] && ! grep -q '"rollForward"' "$RUNTIME_CONFIG"; then
  sed -i 's/"tfm": "net8.0",/"tfm": "net8.0",\n    "rollForward": "Major",/' "$RUNTIME_CONFIG"
  echo "已补 rollForward=Major"
fi

echo "完成：$OUT/Starcloud.Bridge.dll"
