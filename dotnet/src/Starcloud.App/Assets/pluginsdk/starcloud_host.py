# Starcloud 启动器 Python 插件宿主 SDK（规范见 docs/plugin-spec.md）
#
# 用法：启动器以 `python -X utf8 starcloud_host.py <插件目录>` 启动本文件；
# 插件作者只需在插件目录里写 main.py，定义钩子：
#
#   def onLoad(ctx):
#       ctx.ui.register_panel("main", "我的面板", "assets/panel.html")
#
#   def beforeGameLaunch(ctx):
#       ctx.args.append("-Xmx4G")
#
#   def onLauncherEvent(ctx):
#       if ctx.event["name"] == "game.exited":
#           ctx.logger.info("这一局结束了")
#
# 协议（stdin/stdout 行式 JSON）：
#   宿主 → 插件  {"op":"load","permissions":[…],"launcher":{…}}
#                {"op":"call","hook":"beforeGameLaunch"}
#                {"op":"call","hook":"onLauncherEvent","event":{"name":…,"data":…}}
#                {"op":"unload"}
#   插件 → 宿主  {"ok":true,"append":[…],"removeKey":[…],"logs":[[lv,txt],…],
#                "panels":[[id,title,entry],…],"slots":[[id,entry,height],…],
#                "notifications":[[title,msg],…]}
#                {"ok":false,"error":"…"}
#   插件 → 宿主（能力回调）{"op":"host_call","id":7,"api":"instances.list","args":[…]}
#   宿主 → 插件            {"op":"host_reply","id":7,"ok":true,"result":…}
#
# 能力回调让 Python 插件用上与 JS 插件完全相同的能力面（版本 / 实例 / 资源 / 设置 /
# 存储 / 剪贴板 / 确认框 / 事件…），权限校验全在宿主侧完成；未声明的权限调用会收到
# {"ok":false,"error":"插件未声明 … 权限"}，本 SDK 会把它抛成 PermissionError。
#
# 合规声明：本 SDK 不提供、不接管任何账号身份验证能力，也拿不到任何令牌。

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
_SLOTS = []
_NOTIFY = []
_APPEND = []
_REMOVE = []
_CALL_ID = 0


def _host_call(api, args=None):
    """同步调用一项宿主能力：写一行 host_call，读回 host_reply。"""
    global _CALL_ID
    _CALL_ID += 1
    call_id = _CALL_ID
    sys.stdout.write(json.dumps(
        {"op": "host_call", "id": call_id, "api": api, "args": list(args or [])},
        ensure_ascii=False) + "\n")
    sys.stdout.flush()
    while True:
        raw = sys.stdin.readline()
        if not raw:
            raise RuntimeError("宿主已断开连接")
        raw = raw.strip()
        if not raw:
            continue
        try:
            reply = json.loads(raw)
        except Exception:  # noqa: BLE001
            continue
        if reply.get("op") != "host_reply" or reply.get("id") != call_id:
            continue
        if reply.get("ok"):
            return reply.get("result")
        raise PermissionError(reply.get("error") or "宿主调用失败")


class _Capability:
    """一项 ctx 能力的薄封装：把 python 方法名映射到宿主能力名。"""

    def __init__(self, domain):
        self._domain = domain

    def _call(self, method, *args):
        return _host_call(f"{self._domain}.{method}", args)

    def __getattr__(self, item):
        # snake_case → camelCase（宿主侧方法名是 PascalCase，capability 名是 camelCase）
        parts = item.split("_")
        camel = parts[0] + "".join(p.title() for p in parts[1:])

        def invoke(*args):
            return self._call(camel, *args)

        return invoke


class ArgsManager:
    """启动参数增量管理器：仅 append / remove_key，无全量覆写"""

    def append(self, arg):
        if isinstance(arg, str) and arg.strip():
            _APPEND.append(arg.strip())

    def append_game(self, arg):
        self.append(arg)

    def append_jvm(self, arg):
        if isinstance(arg, str) and arg.strip():
            _APPEND.append(arg.strip())

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
    """UI 扩展：面板 / 挂载区 / 通知走声明式上报；确认框与开页走能力回调。"""

    def register_panel(self, panel_id, title, entry):
        _PANELS.append([panel_id, title, entry])

    def register_slot(self, slot_id, entry, height=96):
        _SLOTS.append([slot_id, entry, int(height)])

    def notify(self, title, message):
        if "launcher:notification" not in _PERMS:
            raise PermissionError("未声明 launcher:notification 权限，无法发送通知")
        _NOTIFY.append([title, message])

    def confirm(self, title, message):
        """弹确认框；用户点确认返回 True，取消返回 False（需要 launcher:ui_panel）。"""
        return bool(_host_call("system.confirm", [title, message]))

    def alert(self, title, message):
        """弹提示框（只有一个按钮，需要 launcher:ui_panel）。"""
        _host_call("system.alert", [title, message])

    def open_page(self, page):
        """切到启动器页面：home/versions/download/accounts/saves/logs/settings/nova/multiplayer/plugins"""
        return _host_call("system.openPage", [page])


class Ctx:
    """插件上下文：成员与 JS 插件一一对应（详见 docs/plugin-sdk.md 对照表）。"""

    def __init__(self, permissions, launcher, event=None):
        self.permissions = permissions
        self.launcher = launcher
        self.event = event
        self.logger = Logger()
        self.files = _Capability("files")
        self.args = ArgsManager() if "launcher:game_arguments" in permissions else None
        if "launcher:ui_panel" in permissions or "launcher:notification" in permissions:
            self.ui = UiApi()
        else:
            self.ui = None

        # 能力域：与宿主 ctx 成员同名，未声明权限的域调用会抛 PermissionError
        for domain, perm in (
            ("versions", "launcher:instances_read"),
            ("instances", ("launcher:instances_read", "launcher:instances_write")),
            ("game", "launcher:game_launch"),
            ("content", ("launcher:content_read", "launcher:content_write")),
            ("download", "launcher:download"),
            ("http", "launcher:network"),
            ("settings", ("launcher:settings_read", "launcher:settings_write")),
            ("accounts", "launcher:accounts_read"),
            ("logs", "launcher:logs_read"),
            ("tasks", "launcher:tasks"),
            ("saves", ("launcher:saves_read", "launcher:saves_write")),
            ("multiplayer", "launcher:multiplayer"),
            ("appearance", "launcher:appearance"),
            ("updates", "launcher:updates"),
            ("storage", "launcher:storage"),
            ("system", ("launcher:clipboard", "launcher:shell", "launcher:ui_panel")),
            ("events", "launcher:events"),
        ):
            wanted = perm if isinstance(perm, tuple) else (perm,)
            if any(p in permissions for p in wanted):
                setattr(self, domain, _Capability(domain))
            else:
                setattr(self, domain, None)  # 与 JS 侧 undefined 一致


def _emit(ok, error=None):
    resp = {"ok": ok}
    if error is not None:
        resp["error"] = error
    if ok:
        resp["append"] = _APPEND[:]
        resp["removeKey"] = _REMOVE[:]
        resp["logs"] = _LOGS[:]
        resp["panels"] = _PANELS[:]
        resp["slots"] = _SLOTS[:]
        resp["notifications"] = _NOTIFY[:]
    _APPEND.clear()
    _REMOVE.clear()
    _LOGS.clear()
    _PANELS.clear()
    _SLOTS.clear()
    _NOTIFY.clear()
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
            fn = namespace.get("onLoad")
            try:
                if callable(fn):
                    fn(Ctx(_PERMS, _LAUNCHER))
                _emit(True)
            except Exception as e:  # noqa: BLE001
                _emit(False, f"onLoad 执行失败：{e}")
        elif op == "call":
            hook = req.get("hook")
            ctx = Ctx(_PERMS, _LAUNCHER, event=req.get("event"))
            fn = namespace.get(hook)
            if not callable(fn):
                _emit(True)  # 未定义钩子 = 空操作
                continue
            try:
                fn(ctx)
                _emit(True)
            except Exception as e:  # noqa: BLE001
                _emit(False, f"{hook} 执行失败：{e}")
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
