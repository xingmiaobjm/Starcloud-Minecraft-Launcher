package cn.starcloud.launcher.compose.data

/**
 * 下载调试开关。**只有 debug 自检会写它**（BridgeSelfTestActivity），正式流程不碰。
 *
 * proxyBase 非空时，所有官方下载 URL 会改写成 `<proxyBase>/<路径>`，
 * 配合 adb reverse tcp:8889 tcp:8889 + 电脑侧 tools/forward_proxy.py 使用
 * （宿主机被加速器 TUN 分流、模拟器/真机无法直连 Mojang 时用）。
 */
object DownloadDebug {
    @Volatile
    var proxyBase: String? = null

    /** 非空时清单只从这个 URL 拉（自检用本地假仓库 tools/fake-repo） */
    @Volatile
    var manifestUrl: String? = null
}
