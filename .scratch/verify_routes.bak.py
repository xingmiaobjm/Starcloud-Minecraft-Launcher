import re
from collections import Counter
from pathlib import Path
BRIDGE = Path("dotnet/src/Starcloud.Bridge")

methods_cs = (BRIDGE/"Program.Methods.cs").read_text(encoding="utf-8")
# 抓 t.Xxx("name", Handler) —— Handler 可能是方法组，也可能是 RunXxx
# Streaming 多一个"占用中文案"参数，单独放行
regs = re.findall(r't\.(Request|Concurrent)\("([^"]+)"\s*,\s*(\w+)\s*\)', methods_cs)
regs += re.findall(r't\.Streaming\("([^"]+)"\s*,\s*"[^"]*"\s*,\s*(\w+)\s*\)', methods_cs) and []
stream = [(m[0], m[1]) for m in re.findall(r't\.Streaming\("([^"]+)"\s*,\s*"[^"]*"\s*,\s*(\w+)\s*\)', methods_cs)]
regs = regs + [("Streaming", n, h) for n, h in stream]
print("登记条数:", len(regs))

defined = set()
for f in BRIDGE.glob("Program*.cs"):
    defined |= set(re.findall(r"(?:Task<object\?>|Task) (Handle\w+|Run\w+)\(", f.read_text(encoding="utf-8")))

names = [n for _, n, _ in regs]
dups = [n for n, c in Counter(names).items() if c > 1]
print("重复方法名:", dups)

bad = [(n, h) for _, n, h in regs if h not in defined]
print("登记了但找不到实现:", bad)

used = {h for _, _, h in regs}
unregistered = sorted(d for d in defined if d.startswith("Handle") and d not in used)
print("有实现但未登记:", unregistered)

print("\nkind 分布:", Counter(k for k, _, _ in regs))
