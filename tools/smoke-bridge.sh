#!/usr/bin/env bash
# 桥接冒烟：真实拉起 Starcloud.Bridge，逐条打协议，检查回包与事件。
#
# 用法（主仓库根目录）：
#   bash tools/smoke-bridge.sh                       # 用 Debug 构建
#   DLL=path/to/Starcloud.Bridge.dll bash tools/smoke-bridge.sh
#
# 基线：ok=19 / fail=3（全为预期：未知方法 + 2 个缺参数）/ event=4
set -u
cd "$(dirname "$0")/.."

DLL=${DLL:-dotnet/src/Starcloud.Bridge/bin/Debug/net8.0/Starcloud.Bridge.dll}
if [ ! -f "$DLL" ]; then
  echo "找不到 $DLL，先跑：dotnet build dotnet/src/Starcloud.Bridge/Starcloud.Bridge.csproj -c Debug" >&2
  exit 1
fi

# 沙箱 / 只读家目录下，默认数据根 ~/.local/share/Starcloud 不可写，
# 桥接会在启动时抛 IOException 并退出（退出码 134），必须改到可写位置。
export STARCLOUD_DATA_DIR=${STARCLOUD_DATA_DIR:-"$PWD/.scratch/data"}
mkdir -p "$STARCLOUD_DATA_DIR"

printf '%s\n' \
  '{"id":1,"method":"ping","params":{}}' \
  '{"id":2,"method":"bridge.methods","params":{}}' \
  '{"id":3,"method":"settings.get","params":{}}' \
  '{"id":4,"method":"accounts.list","params":{}}' \
  '{"id":5,"method":"catalog.versions","params":{}}' \
  '{"id":6,"method":"catalog.installed","params":{}}' \
  '{"id":7,"method":"version.settings","params":{"versionId":"1.20.1"}}' \
  '{"id":8,"method":"version.detail","params":{"versionId":"1.20.1"}}' \
  '{"id":9,"method":"versions.backupList","params":{}}' \
  '{"id":10,"method":"java.list","params":{}}' \
  '{"id":11,"method":"runtime.state","params":{}}' \
  '{"id":12,"method":"multiplayer.state","params":{}}' \
  '{"id":13,"method":"plugins.list","params":{}}' \
  '{"id":14,"method":"skins.list","params":{}}' \
  '{"id":15,"method":"saves.list","params":{}}' \
  '{"id":16,"method":"nova.config.get","params":{}}' \
  '{"id":17,"method":"lab.palette","params":{}}' \
  '{"id":18,"method":"starcloud.state","params":{}}' \
  '{"id":19,"method":"not.exist","params":{}}' \
  '{"id":20,"method":"lab.assets","params":{}}' \
  '{"id":21,"method":"skins.bs6.status","params":{}}' \
  'not json at all' \
  '{"id":23,"method":"ping","params":{}}' \
  | dotnet "$DLL"
