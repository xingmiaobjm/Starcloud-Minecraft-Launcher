// Starcloud 安卓版 · Kotlin/Compose 重写
// UI 对齐 Win 端（WinUI 3）：44dp 顶栏（品牌+版本徽章）+ 左侧图标导航栏 + 内容区；
// 业务逻辑按 README 里程碑从 C# Core 渐进移植（M0-M2 已交付版本/下载域）。
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
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.VersionsRepository
import cn.starcloud.launcher.compose.game.GameLauncher
import cn.starcloud.launcher.compose.screens.DownloadScreen
import cn.starcloud.launcher.compose.screens.HomeScreen
import cn.starcloud.launcher.compose.screens.VersionsScreen
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import cn.starcloud.launcher.compose.ui.theme.FluentTheme
import java.io.File

/** 左侧导航项（对齐 Win 端 NavigationView 菜单的常用子集） */
private data class NavItem(val key: String, val label: String, val glyph: String)

private val NAV_ITEMS = listOf(
    NavItem("home", "首页", "⌂"),
    NavItem("versions", "版本", "☰"),
    NavItem("download", "下载", "⤓"),
    NavItem("multiplayer", "联机", "∞"),
)

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            FluentTheme {
                AppRoot()
            }
        }
    }

}

/** 真实启动入口：GameLauncher 全链（组件部署 → SDL 活动 → JVM）在专门线程上执行 */
private fun startLaunch(context: android.content.Context, versionId: String, onState: (String) -> Unit) {
    onState("启动中…")
    Thread {
        GameLauncher.launch(context, versionId, "Player", object : GameLauncher.Listener {
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
private fun AppRoot() {
    var selected by remember { mutableStateOf("home") }
    var refreshKey by remember { mutableIntStateOf(0) }
    var launchStatus by remember { mutableStateOf<String?>(null) }
    var launchBusy by remember { mutableStateOf(false) }
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val filesRoot = remember { ctx.getExternalFilesDir(null) ?: ctx.filesDir }

    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
        // ── 顶栏：品牌标记 + Starcloud + 版本徽章（对齐 Win 端 44dp 顶栏） ──
        Row(
            Modifier.fillMaxWidth().height(FluentDimens.TopBarHeight).padding(horizontal = 16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                Modifier.size(18.dp).background(
                    FluentColors.Accent,
                    RoundedCornerShape(FluentDimens.CornerRadiusSmall),
                )
            )
            Spacer(Modifier.width(8.dp))
            Text(
                "Starcloud",
                fontSize = 14.sp,
                fontWeight = androidx.compose.ui.text.font.FontWeight.Medium,
            )
            Spacer(Modifier.width(8.dp))
            Text(
                "测试版 V0.0.0",
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                modifier = Modifier
                    .background(MaterialTheme.colorScheme.surfaceContainerHigh, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                    .padding(horizontal = 6.dp, vertical = 2.dp),
            )
        }

        // ── 主体：左侧图标导航栏 + 内容区（对齐 Win 端 NavigationView 布局） ──
        Row(Modifier.fillMaxSize()) {
            FluentNavRail(selected = selected, onSelect = { selected = it })
            Box(Modifier.weight(1f)) {
                when (selected) {
                    "home" -> HomeScreen(
                        filesRoot = filesRoot,
                        probeVersions = { probeVersions(filesRoot) },
                        probeStorage = { probeStorage() },
                        onNavigate = { selected = it },
                        onLaunchVersion = { id ->
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
                    )
                    else -> PlaceholderPage(selected)
                }
            }
        }
    }
}

/** 左侧图标导航栏：选中项 accent 高亮 + 圆角底色（对齐 Win 端 NavigationView 选中态） */
@Composable
private fun FluentNavRail(selected: String, onSelect: (String) -> Unit) {
    Column(
        Modifier
            .width(FluentDimens.NavRailWidth)
            .fillMaxHeight()
            .padding(top = 8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(2.dp),
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
                    .padding(vertical = 8.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(
                    item.glyph,
                    fontSize = 18.sp,
                    color = if (isSelected) FluentColors.Accent
                    else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    item.label,
                    fontSize = 11.sp,
                    color = if (isSelected) FluentColors.Accent
                    else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                )
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
