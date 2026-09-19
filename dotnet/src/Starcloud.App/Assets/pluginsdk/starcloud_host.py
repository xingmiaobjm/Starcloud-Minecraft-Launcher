# Starcloud 启动器 Python 插件宿主 SDK（plugin-spec.md）
# 用法：启动器以 `python -X utf8 starcloud_host.py <插件目录>` 启动本文件；
# 插件作者只需在插件目录写 main.py，定义 onLoad / onUnload / beforeGameLaunch 等钩子：
#
#   def onLoad(ctx):
#       ctx.ui and ctx.ui.register_panel("main", "我的面板", "assets/panel.html")
#
#   def beforeGameLaunch(ctx):
#       ctx.args.append("-Xmx4G")   # 仅追加；身份参数由启动器拦截
#
# 协议（stdin/stdout 行式 JSON）：
#   请求  {"op":"load","permissions":[...],"launcher":{...}}
#         {"op":"call","hook":"beforeGameLaunch"}
#         {"op":"unload"}
#   响应  {"ok":true,"append":[...],"removeKey":[...],"logs":[[lv,txt],...],
#          "panels":[[id,title,entry],...],"notifications":[[title,msg],...]}
#         {"ok":false,"error":"..."}
#
# 合规声明：本 SDK 仅提供启动参数扩展与 UI 扩展能力；不提供、不接管任何账号身份验证功能。
# 技术限制：Python 进程外运行，ctx.files（受沙箱文件访问）暂不开放。

import io
import json
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", line_buffering=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", line_buffering=True)

_PERMS = []
_LAUNCHER = {}
_LOGS = []
_PANELS = []
_NOTIFY = []
_APPEND = []
_REMOVE = []


class ArgsManager:
    """启动参数增量管理器：仅 append / remove_key，无全量覆写"""

    def append(self, arg):
        if isinstance(arg, str) and arg.strip():
            _APPEND.append(arg.strip())

    def append_game(self, arg):
        self.append(arg)

    def append_jvm(self, arg):
        if isinstance(arg, str) and arg.strip():
            _APPEND.append(arg.strip())  # 协议层不分流 jvm/game，宿主按 ctx.args 语义收集
    def remove_key(self, key):
        if isinstance(key, str) and key.strip():
            _REMOVE.append(key.strip())


class Logger:
    def info(self, msg):
        _LOGS.append(["INFO", str(msg)])

    def warn(self, msg):
        _LOGS.append(["WARN", str(msg)])

    def error(self, msg):
        _LOGS.append(["ERROR", str(msg)])


class UiApi:
    def register_panel(self, panel_id, title, entry):
        _PANELS.append([panel_id, title, entry])

    def notify(self, title, message):
        if "launcher:notification" not in _PERMS:
            raise RuntimeError("未声明 launcher:notification 权限，无法发送通知")
        _NOTIFY.append([title, message])


class Ctx:
    def __init__(self, permissions, launcher):
        self.permissions = permissions
        self.launcher = launcher
        self.files = None  # 进程外运行时暂不开放（沙箱限制，见规范）
        if "launcher:game_arguments" in permissions:
            self.args = ArgsManager()
        else:
            self.args = None
        if "launcher:ui_panel" in permissions or "launcher:notification" in permissions:
            self.ui = UiApi()
        else:
            self.ui = None
        self.logger = Logger()


def _emit(ok, error=None):
    resp = {"ok": ok}
    if error is not None:
        resp["error"] = error
    if ok:
        resp["append"] = _APPEND[:]
        resp["removeKey"] = _REMOVE[:]
        resp["logs"] = _LOGS[:]
        resp["panels"] = _PANELS[:]
        resp["notifications"] = _NOTIFY[:]
    _APPEND.clear(); _REMOVE.clear(); _LOGS.clear()
    _PANELS.clear(); _NOTIFY.clear()
    sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main():
    if len(sys.argv) < 2:
        _emit(False, "缺少插件目录参数")
        return
    plugin_dir = sys.argv[1]
    main_py = os.path.join(plugin_dir, "main.py")
    if not os.path.isfile(main_py):
        _emit(False, "插件缺少 main.py")
        return

    namespace = {}
    try:
        with open(main_py, "r", encoding="utf-8") as f:
            code = f.read()
        exec(compile(code, main_py, "exec"), namespace)
    except Exception as e:  # noqa: BLE001
        _emit(False, f"main.py 解析失败：{e}")
        return

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            req = json.loads(raw)
        except Exception as e:  # noqa: BLE001
            _emit(False, f"请求解析失败：{e}")
            continue

        op = req.get("op")
        if op == "load":
            _PERMS[:] = req.get("permissions") or []
            _LAUNCHER.update(req.get("launcher") or {})
            ctx = Ctx(_PERMS, _LAUNCHER)
            fn = namespace.get("onLoad")
            try:
                if callable(fn):
                    fn(ctx)
                _emit(True)
            except Exception as e:  # noqa: BLE001
                _emit(False, f"onLoad 执行失败：{e}")
        elif op == "call":
            hook = req.get("hook")
            ctx = Ctx(_PERMS, _LAUNCHER)
            fn = namespace.get(hook)
            if hook in ("onLoad", "onUnload", "beforeGameLaunch") or callable(fn):
                try:
                    if callable(fn):
                        fn(ctx)
                    _emit(True)
                except Exception as e:  # noqa: BLE001
                    _emit(False, f"{hook} 执行失败：{e}")
            else:
                _emit(True)  # 未定义钩子 = 空操作
        elif op == "unload":
            fn = namespace.get("onUnload")
            try:
                if callable(fn):
                    fn(Ctx(_PERMS, _LAUNCHER))
                _emit(True)
            except Exception as e:  # noqa: BLE001
                _emit(False, f"onUnload 执行失败：{e}")
            return
        else:
            _emit(False, f"未知操作：{op}")


if __name__ == "__main__":
    main()
