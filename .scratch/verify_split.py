#!/usr/bin/env python3
"""回归校验：HEAD 版 Program.cs 与拆分后 partial 文件的「成员体多重集」比对。

按括号深度切分顶层成员，每个成员体规范化空白后作为比对单元（不依赖方法名）。
预期差异严格为：
  · 丢失 = 恰好 1 项（HEAD 的 DispatchAsync，其 141 个 case 已外置）
  · 新增 = 141 个 HandleXxxAsync + 1 个新的 DispatchAsync 路由 = 142 项
"""
import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path("/home/seasky/codes/projects/Starcloud-Minecraft-Launcher")
BRIDGE = ROOT / "dotnet/src/Starcloud.Bridge"
HEAD = ROOT / ".scratch/HEAD_Program.cs"


def strip_comments(src: str) -> str:
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


def norm(text: str) -> str:
    return re.sub(r"\s+", "", text)


def member_bodies(src: str) -> list[str]:
    """按顶层成员切分类体，返回规范化后的成员体列表。"""
    lines = strip_comments(src).split("\n")
    cls = next(i for i, l in enumerate(lines)
               if re.match(r"^\s*public static (partial )?class Program\s*$", l))
    open_i = next(i for i in range(cls, len(lines)) if lines[i].strip() == "{")

    out, buf, depth = [], [], 0
    for i in range(open_i + 1, len(lines)):
        line = lines[i]
        s = line.strip()
        if not buf and not s:
            continue
        buf.append(line)
        depth += line.count("{") - line.count("}")
        if depth == 0 and s and s[-1] in ";}":
            body = norm("\n".join(buf))
            if body not in ("", "}", "};"):
                out.append(body)
            buf = []
    return out


head = Counter(member_bodies(HEAD.read_text(encoding="utf-8")))
files = sorted(BRIDGE.glob("Program*.cs"))
cur = Counter()
per_file = {}
for f in files:
    bodies = member_bodies(f.read_text(encoding="utf-8"))
    per_file[f.name] = len(bodies)
    cur.update(bodies)

lost = head - cur
extra = cur - head

print(f"HEAD 成员 {sum(head.values())} 个；拆分后 {sum(cur.values())} 个，分布 {len(files)} 个文件")
print("  每文件成员数：" + "  ".join(f"{k.replace('Program.','').replace('.cs','')}={v}"
                                   for k, v in per_file.items()) + "\n")

print(f"[丢失] {sum(lost.values())} 项")
for body, n in lost.items():
    print(f"   ×{n}  {body[:100]}")

handles = [b for b in extra if re.match(r"^privatestaticasyncTask<object\?>Handle\w+Async\(", b)]
dispatch = [b for b in extra if b.startswith("privatestaticasyncTask<object?>DispatchAsync(")]
other = [b for b in extra if b not in handles and b not in dispatch]

print(f"\n[新增] {sum(extra.values())} 项 = Handle {len(handles)} + DispatchAsync {len(dispatch)} + 其它 {len(other)}")
for b in other[:10]:
    print(f"   ?  {b[:100]}")

ok = (sum(lost.values()) == 1 and list(lost)[0].startswith("privatestaticasyncTask<object?>DispatchAsync(")
      and len(handles) == 141 and len(dispatch) == 1 and not other)
print(f"\n结论：{'✅ 成员级无损（仅 DispatchAsync 改写 + 141 个 Handle 外置）' if ok else '❌ 存在差异'}")
sys.exit(0 if ok else 1)
