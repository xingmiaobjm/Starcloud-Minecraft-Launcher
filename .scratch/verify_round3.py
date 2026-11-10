#!/usr/bin/env python3
"""第三刀回归校验：以第二刀终态（.scratch/backup/）为基准，比对当前 Bridge 的成员多重集。

允许的差异必须逐条可解释：
  · 丢失 = IsLongRunning / DispatchAsync（旧 switch 路由）——已被方法表取代
  · 新增 = bridge.methods 自省 handler + 方法表构建器 + 表字段/属性
  · 变更 = Main（分发段）/ RunInstallAsync / RunMultiplayerInstallAsync（签名）
           / HandleVersionsDeleteAsync（改用 Methods.IsRunning）/ PluginHost 的 LauncherVersion
"""
import re, sys
from collections import Counter
from pathlib import Path

ROOT = Path("/home/seasky/codes/projects/Starcloud-Minecraft-Launcher")
BRIDGE = ROOT / "dotnet/src/Starcloud.Bridge"
BASE = ROOT / ".scratch/backup"

def strip_comments(s):
    s = re.sub(r"/\*.*?\*/", "", s, flags=re.S)
    return re.sub(r"//[^\n]*", "", s)

def norm(t): return re.sub(r"\s+", "", t)

def member_bodies(src):
    lines = strip_comments(src).split("\n")
    try:
        cls = next(i for i, l in enumerate(lines) if re.match(r"^\s*public static (partial )?class Program\s*$", l))
    except StopIteration:
        return []
    open_i = next(i for i in range(cls, len(lines)) if lines[i].strip() == "{")
    out, buf, depth = [], [], 0
    for i in range(open_i + 1, len(lines)):
        line = lines[i]; s = line.strip()
        if not buf and not s: continue
        buf.append(line)
        depth += line.count("{") - line.count("}")
        if depth == 0 and s and s[-1] in ";}":
            b = norm("\n".join(buf))
            if b not in ("", "}", "};"): out.append(b)
            buf = []
    return out

base = Counter(); base_names = {}
for f in sorted(BASE.glob("Program*.cs")):
    for b in member_bodies(f.read_text(encoding="utf-8")):
        base[b] += 1; base_names[b] = f.name

cur = Counter(); cur_names = {}
for f in sorted(BRIDGE.glob("Program*.cs")):
    for b in member_bodies(f.read_text(encoding="utf-8")):
        cur[b] += 1; cur_names[b] = f.name

lost = base - cur; extra = cur - base
print(f"基准（第二刀终态）成员 {sum(base.values())} 个 → 当前 {sum(cur.values())} 个\n")

print(f"[丢失] {sum(lost.values())} 项")
for b in lost: print(f"   -{base_names[b]}: {b[:110]}")

print(f"\n[新增] {sum(extra.values())} 项")
for b in extra: print(f"   +{cur_names[b]}: {b[:110]}")

# 路由/方法表一致性
methods_cs = (BRIDGE / "Program.Methods.cs").read_text(encoding="utf-8")
regs = re.findall(r't\.(Request|Concurrent|Streaming)\("([^"]+)"', methods_cs)
handles = set()
for f in BRIDGE.glob("Program*.cs"):
    handles |= set(re.findall(r"private static async Task<object\?> (Handle\w+Async)\(", f.read_text(encoding="utf-8")))
print(f"\n方法表登记 {len(regs)} 条（request={sum(1 for k,_ in regs if k=='Request')}, "
      f"concurrent={sum(1 for k,_ in regs if k=='Concurrent')}, streaming={sum(1 for k,_ in regs if k=='Streaming')}）")
print(f"Handle*Async 定义 {len(handles)} 个")
dups = [n for n, c in Counter(n for _, n in regs).items() if c > 1]
print(f"重复登记 {len(dups)} 个 {dups}")
missing = [h for h in handles if not any(h == "Handle" + "".join(p.capitalize() for p in re.split(r"[._]", n)) + "Async" for _, n in regs)]
print(f"定义了但未登记 {len(missing)} 个 {missing[:5]}")
