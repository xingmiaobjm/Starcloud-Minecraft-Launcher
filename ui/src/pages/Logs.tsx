// 日志页：读取最近游戏日志 + 订阅实时输出

import { useEffect, useRef, useState } from "preact/hooks";
import { getGameLogs } from "../api";

interface Props {
  liveLines: string[];
}

export function Logs({ liveLines }: Props) {
  const [tail, setTail] = useState<string[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const boxRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    getGameLogs()
      .then((text) => {
        setTail(text ? text.split("\n").filter(Boolean) : []);
        setLoaded(true);
      })
      .catch((e) => {
        setErr(String(e));
        setLoaded(true);
      });
  }, []);

  // 实时行追加在历史尾部之后
  const all = [...tail, ...liveLines];

  useEffect(() => {
    const el = boxRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [all.length]);

  return (
    <div class="page">
      <div class="page-title">游戏日志</div>
      <div class="page-desc">显示 .minecraft/logs/latest.log 尾部内容，游戏运行时实时追加</div>

      {err && <div class="error-box" style="margin-bottom:14px">{err}</div>}

      <div class="game-log" ref={boxRef}>
        {!loaded ? (
          "加载中…"
        ) : all.length === 0 ? (
          "（暂无日志）"
        ) : (
          all.map((l, i) => <div key={i}>{l}</div>)
        )}
      </div>
    </div>
  );
}
