#!/usr/bin/env python3
"""方法表一致性校验：Program.Methods.cs 的登记 与 Program*.cs 的实现 必须一一对应。

用法（在主仓库根目录）：
    python3 .scratch/verify_routes.py

三项输出必须全部为空：
    · 重复方法名           —— 同名登记两次（BridgeMethodTable.Add 运行时也会抛，这里是编译前发现）
    · 登记了但找不到实现   —— 方法表指向一个不存在的 Handle*/Run* 方法
    · 有实现但未登记       —— 写了 Handle 却忘了登记（老问题：实现了但调不到）
"""
import re
import sys
from collections import Counter
from pathlib import Path

BRIDGE = Path("dotnet/src/Starcloud.Bridge")

methods_cs = (BRIDGE / "Program.Methods.cs").read_text(encoding="utf-8")

# t.Request("name", Handler) / t.Concurrent("name", Handler)
regs = [(k, n, h) for k, n, h in
        re.findall(r't\.(Request|Concurrent)\("([^"]+)"\s*,\s*(\w+)\s*\)', methods_cs)]
# t.Streaming("name", "占用中文案", Starter) —— 中间多一个字符串
regs += [("Streaming", n, h) for n, h in
         re.findall(r't\.Streaming\("([^"]+)"\s*,\s*"[^"]*"\s*,\s*(\w+)\s*\)', methods_cs)]

print(f"登记条数: {len(regs)}")

defined = set()
for f in BRIDGE.glob("Program*.cs"):
    defined |= set(re.findall(r"(?:Task<object\?>|Task) (Handle\w+|Run\w+)\(",
                              f.read_text(encoding="utf-8")))

names = [n for _, n, _ in regs]
dups = [n for n, c in Counter(names).items() if c > 1]
bad = [(n, h) for _, n, h in regs if h not in defined]
used = {h for _, _, h in regs}
unregistered = sorted(d for d in defined if d.startswith("Handle") and d not in used)

print(f"重复方法名: {dups}")
print(f"登记了但找不到实现: {bad}")
print(f"有实现但未登记: {unregistered}")
print(f"kind 分布: {dict(Counter(k for k, _, _ in regs))}")

ok = not dups and not bad and not unregistered
print("结论：" + ("✅ 方法表与实现一一对应" if ok else "❌ 存在不一致"))
sys.exit(0 if ok else 1)
