// Hello 面板示例插件（docs/plugin-sdk.md §1）
// 仅使用规范白名单能力：参数追加 / 通知 / UI 面板；身份类参数会被本体拦截丢弃。

function onLoad(ctx) {
    ctx.logger.info("hello 插件已加载，启动器 " + ctx.launcher.version);
    ctx.ui.registerPanel("main", "Hello 面板", "assets/panel.html");
}

function onUnload(ctx) {
    ctx.logger.info("hello 插件已卸载");
}

function beforeGameLaunch(ctx) {
    // 演示：只追加。ctx.args 没有 overwrite——接口层面就不存在全量覆写
    ctx.args.append("-Dhello.enabled=true");
    ctx.ui.notify("Hello 插件", "已为本次启动追加演示参数");
}
