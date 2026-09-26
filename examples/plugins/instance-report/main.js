// 实例与战绩简报示例插件（docs/plugin-sdk.md §5）
// 演示本轮扩展的能力面：读实例 / 读身份 / 读任务 / 订阅事件 / 私有存储。
// 全程只读 + 只写自己的 data 目录，不碰任何凭证，也不做不可逆操作。

function onLoad(ctx) {
    const instances = ctx.instances.list();          // launcher:instances_read
    const running = instances.filter(v => v.running);
    ctx.logger.info(`共 ${instances.length} 个实例，` +
        (running.length ? `正在运行：${running[0].id}` : "当前没有游戏在跑"));

    const me = ctx.accounts.current();               // launcher:accounts_read（只读身份）
    ctx.logger.info(me.ready
        ? `本局身份：${me.name}（${me.type}）`
        : `身份未就绪：${me.reason}`);

    // 订阅事件：游戏起停都会走 onLauncherEvent
    ctx.events.on("game.launched");                  // launcher:events
    ctx.events.on("game.exited");
    ctx.ui.notify("实例简报", `已就绪，共 ${instances.length} 个实例`);
}

function beforeGameLaunch(ctx) {
    // 仍然只允许增量：身份参数会被本体拦截丢弃
    ctx.args.append("-Dstarcloud.report=true");      // launcher:game_arguments
}

function onLauncherEvent(ctx) {
    const data = JSON.parse(ctx.event.data);         // data 是 JSON 字符串
    if (ctx.event.name === "game.launched") {
        ctx.logger.info(`开局：${data.versionId}（pid ${data.pid}）`);
        return;
    }
    // 退出：累计游玩次数，并顺带看一眼任务中心里还剩什么在跑
    const rounds = (ctx.storage.get("rounds") || 0) + 1;
    ctx.storage.set("rounds", rounds);               // launcher:storage
    ctx.storage.set("lastVersion", data.versionId);

    const active = ctx.tasks.list().filter(t => t.state === "Running");
    ctx.logger.info(`收工：${data.versionId}，累计 ${rounds} 局；` +
        `进行中的任务 ${active.length} 个`);

    // 账号快照（只读）也可以一起留档；不包含任何令牌
    ctx.storage.set("accounts", ctx.accounts.list().map(a => `${a.type}:${a.name}`));

    if (rounds % 5 === 0) {
        ctx.ui.notify("战绩简报", `已经玩了 ${rounds} 局，注意休息`);  // launcher:notification
    }
}
