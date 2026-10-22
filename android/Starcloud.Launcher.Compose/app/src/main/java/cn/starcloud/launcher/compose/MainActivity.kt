// Starcloud 安卓版 · Kotlin/Compose 重写
// 手机端壳（对齐 FCL）：横屏锁定 + 左侧图标导航栏 + 内容区占满全高（无标题栏）。
// 业务逻辑按 README 里程碑从 C# Core 渐进移植（版本/下载/启动链/账户/设置已交付）。
package cn.starcloud.launcher.compose

import android.os.Bundle
import android.os.Environment
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.AccountsStore
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.data.VersionsRepository
import cn.starcloud.launcher.compose.game.GameLauncher
import cn.starcloud.launcher.compose.screens.AccountsScreen
import cn.starcloud.launcher.compose.data.ManifestEntry
import cn.starcloud.launcher.compose.screens.LoaderSelectScreen
import cn.starcloud.launcher.compose.screens.DownloadScreen
import cn.starcloud.launcher.compose.screens.HomeScreen
import cn.starcloud.launcher.compose.screens.SettingsScreen
import cn.starcloud.launcher.compose.screens.VersionsScreen
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import cn.starcloud.launcher.compose.ui.theme.FluentTheme
import cn.starcloud.launcher.compose.ui.theme.HarmonyIconFont
import java.io.File

/** 导航项：手机底部栏与平板侧栏共用；friends = 好友（联机并入其第二个标签） */
private data class NavItem(val key: String, val label: String, val glyph: String, val phone: Boolean = true)

// 图标码位与 Win 端 BuiltinShellPlugin 一致（HarmonyOS 图标字体，随包发布）
private val NAV_ITEMS = listOf(
    NavItem("home", "启动", "\uEC77"),
    NavItem("versions", "版本", "\uEA94"),
    NavItem("download", "下载", "\uE90C"),
    NavItem("accounts", "账户", "\uEB8F"),
    NavItem("friends", "好友", "\uEB91"),
    NavItem("settings", "设置", "\uEC04"),
)

class MainActivity : ComponentActivity() {

    /** 悬浮窗「邀请好友」跳转请求：Pair(目标导航, 好友页标签) */
    private val navRequest = androidx.compose.runtime.mutableStateOf<Pair<String, Int>?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 跨设备 Agent：设置里开启过就随启动器常驻（供电脑端 Nova / 跨设备调用）
        if (LauncherPrefs.agentEnabled(this)) {
            cn.starcloud.launcher.compose.game.AgentService.start(this)
        }
        // 联机悬浮窗：开关开着且已授权就随启动器常驻
        if (LauncherPrefs.multiplayerOverlay(this) &&
            cn.starcloud.launcher.compose.game.FloatingOverlayService.canDraw(this)
        ) {
            cn.starcloud.launcher.compose.game.FloatingOverlayService.start(this)
        }
        consumeNavIntent(intent)
        setContent {
            val ctx = LocalContext.current
            var themeMode by remember { mutableStateOf(LauncherPrefs.theme(ctx)) }
            FluentTheme(mode = themeMode) {
                AppRoot(
                    themeMode = themeMode,
                    onThemeChange = {
                        themeMode = it
                        LauncherPrefs.setTheme(ctx, it)
                    },
                    navRequest = navRequest,
                )
            }
        }
    }

    /** 悬浮窗「邀请好友」→ 跳好友页·联机标签 */
    private fun consumeNavIntent(intent: android.content.Intent?) {
        if (intent?.getStringExtra(cn.starcloud.launcher.compose.game.FloatingOverlayService.EXTRA_OPEN_TAB) == "multiplayer") {
            navRequest.value = "friends" to 2
        }
        // 直达触摸布局编辑器（调试/深链）
        if (intent?.getStringExtra("open") == "touch_layout") {
            startActivity(
                android.content.Intent(this, cn.starcloud.launcher.compose.game.TouchLayoutActivity::class.java),
            )
        }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        consumeNavIntent(intent)
    }
}

/** 真实启动入口：GameLauncher 全链（组件部署 → SDL 活动 → JVM）在专门线程上执行 */
private fun startLaunch(context: android.content.Context, versionId: String, onState: (String) -> Unit) {
    val playerName = AccountsStore.selected(context)?.name ?: LauncherPrefs.playerName(context)
    onState("启动中…")
    Thread {
        GameLauncher.launch(context, versionId, playerName, object : GameLauncher.Listener {
            override fun onLog(line: String) {
                android.util.Log.i("StarcloudFlow", line)
            }

            override fun onProgress(phase: String) {
                onState(phase)
            }

            override fun onFinished(exitCode: Int, error: String?) {
                onState(
                    when {
                        error != null -> "启动失败：$error"
                        else -> "游戏已退出（$exitCode）"
                    }
                )
            }
        })
        // 游戏已拉起：开关开着就自动创建联机房间（等待游戏内「对局域网开放」）
        cn.starcloud.launcher.compose.data.MultiplayerController.onGameLaunched(context, playerName)
    }.apply { name = "starcloud-launch" }.start()
}

/** 首页自检探针：已装版本数（真实扫描 versions 目录） */
private fun probeVersions(filesRoot: File): String {
    val n = VersionsRepository(filesRoot).list().size
    return if (n > 0) "已装 $n 个版本" else "未安装"
}

/** 首页自检探针：外部存储就绪态 */
private fun probeStorage(): String =
    if (Environment.getExternalStorageState() == Environment.MEDIA_MOUNTED) "存储就绪" else "存储不可用"

@Composable
private fun AppRoot(
    themeMode: String,
    onThemeChange: (String) -> Unit,
    navRequest: androidx.compose.runtime.State<Pair<String, Int>?>,
) {
    var selected by remember { mutableStateOf("home") }
    var friendsTab by remember { mutableIntStateOf(0) }
    LaunchedEffect(navRequest.value) {
        navRequest.value?.let { (nav, tab) ->
            selected = nav
            friendsTab = tab
        }
    }
    var refreshKey by remember { mutableIntStateOf(0) }
    var launchStatus by remember { mutableStateOf<String?>(null) }
    var launchBusy by remember { mutableStateOf(false) }
    var loaderEntry by remember { mutableStateOf<ManifestEntry?>(null) }
    val ctx = LocalContext.current
    val filesRoot = remember { ctx.getExternalFilesDir(null) ?: ctx.filesDir }
    val playerName = remember(refreshKey) {
        AccountsStore.selected(ctx)?.name ?: LauncherPrefs.playerName(ctx)
    }

    // ── 主体：左侧图标导航栏 + 内容区（内容占满全高，无标题栏） ──
    Row(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background),
    ) {
        // 切换导航 = 打断内容区所有覆盖界面（安装二级页等）
        FluentNavRail(selected = selected, onSelect = {
            selected = it
            loaderEntry = null
        })
        Box(Modifier.weight(1f)) {
            when (selected) {
                "home" -> HomeScreen(
                    filesRoot = filesRoot,
                    playerName = playerName,
                    probeVersions = { probeVersions(filesRoot) },
                    probeStorage = { probeStorage() },
                    onNavigate = { selected = it },
                    onLaunchVersion = { id ->
                        LauncherPrefs.setLastVersion(ctx, id)
                        startLaunch(ctx, id) { state ->
                            launchStatus = state
                            launchBusy = state.startsWith("启动") && !state.startsWith("启动失败")
                        }
                    },
                    launchStatus = launchStatus,
                    launchBusy = launchBusy,
                )
                "versions" -> VersionsScreen(VersionsRepository(filesRoot), refreshKey)
                "download" -> DownloadScreen(
                    cacheFile = File(filesRoot, "cache/version_manifest.json"),
                    filesRoot = filesRoot,
                    refreshKey = refreshKey,
                    onStartDownload = { loaderEntry = it },
                )
                "accounts" -> AccountsScreen(
                    context = ctx,
                    refreshKey = refreshKey,
                    onChanged = { refreshKey++ },
                )
                "friends" -> cn.starcloud.launcher.compose.screens.FriendsScreen(
                    refreshKey = refreshKey,
                    initialTab = friendsTab,
                )
                "settings" -> SettingsScreen(
                    context = ctx,
                    themeMode = themeMode,
                    onThemeChange = onThemeChange,
                )
                else -> PlaceholderPage(selected)
            }
            // 安装二级页：覆盖内容区（导航栏保留）
            loaderEntry?.let { entry ->
                LoaderSelectScreen(
                    entry = entry,
                    filesRoot = filesRoot,
                    onDone = {
                        loaderEntry = null
                        refreshKey++
                    },
                    onBack = { loaderEntry = null },
                )
            }
        }
    }
}

/** 平板左侧图标导航栏：选中项 accent 高亮 + 圆角底色（对齐 Win 端 NavigationView 选中态） */
@Composable
private fun FluentNavRail(selected: String, onSelect: (String) -> Unit) {
    Column(
        Modifier
            .width(FluentDimens.NavRailWidth)
            .fillMaxHeight()
            .padding(vertical = 10.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.SpaceBetween,
    ) {
        NAV_ITEMS.forEach { item ->
            val isSelected = selected == item.key
            Column(
                Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 8.dp)
                    .background(
                        if (isSelected) MaterialTheme.colorScheme.surfaceContainerHigh else Color.Transparent,
                        RoundedCornerShape(FluentDimens.CornerRadius),
                    )
                    .clickableNoRipple { onSelect(item.key) }
                    .padding(vertical = 5.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Box(
                    Modifier.size(width = 34.dp, height = 28.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        item.glyph,
                        fontFamily = HarmonyIconFont,
                        fontSize = 22.sp,
                        color = if (isSelected) FluentColors.Accent
                        else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                    )
                }
            }
        }
    }
}

/** 无涟漪点击（Material 水波破坏 Fluent 观感）；pointerInput 不受组合缓存限制 */
private fun Modifier.clickableNoRipple(onClick: () -> Unit): Modifier =
    this.pointerInput(Unit) { detectTapGestures(onTap = { onClick() }) }

@Composable
private fun PlaceholderPage(key: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text("「" + key + "」页随里程碑交付", color = MaterialTheme.colorScheme.primary)
    }
}
