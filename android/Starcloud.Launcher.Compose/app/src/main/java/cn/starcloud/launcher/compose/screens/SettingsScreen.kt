// 设置页（M5 先行版）：外观主题 + 内存上限 + 关于。
// 其余设置（Java 管理 / 下载源 / 插件…）随后续里程碑接入。
package cn.starcloud.launcher.compose.screens

import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens

@Composable
fun SettingsScreen(
    context: Context,
    themeMode: String,
    onThemeChange: (String) -> Unit,
) {
    val autoMb = remember { LauncherPrefs.autoMaxMemoryMb(context) }
    var autoMode by remember { mutableStateOf(LauncherPrefs.maxMemoryMb(context) == 0) }
    var memoryMb by remember { mutableFloatStateOf(LauncherPrefs.effectiveMaxMemoryMb(context).toFloat()) }

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        PageHeader(title = "设置", actionLabel = "", onAction = {})

        // ── 外观 ──
        SectionCard("外观") {
            Text("主题", fontSize = 14.sp)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ThemeOption("跟随系统", themeMode == "system") { onThemeChange("system") }
                ThemeOption("浅色", themeMode == "light") { onThemeChange("light") }
                ThemeOption("深色", themeMode == "dark") { onThemeChange("dark") }
            }
        }

        // ── 游戏 ──
        SectionCard("游戏") {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("最大内存", fontSize = 14.sp)
                Text(
                    (if (autoMode) "自动 · " else "") + "${memoryMb.toInt()} MB",
                    fontSize = 12.sp,
                    color = FluentColors.Accent,
                )
            }
            Slider(
                value = memoryMb,
                onValueChange = {
                    memoryMb = it
                    autoMode = false
                    LauncherPrefs.setMaxMemoryMb(context, it.toInt())
                },
                valueRange = 512f..8192f,
                steps = ((8192 - 512) / 256 - 1).coerceAtLeast(0),
            )
            Text(
                "自动模式按设备内存分档（本机建议 ${autoMb} MB）；拖动滑条改为手动。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
            )
            if (!autoMode) {
                OutlinedButton(
                    onClick = {
                        LauncherPrefs.setMaxMemoryMb(context, 0)
                        memoryMb = autoMb.toFloat()
                        autoMode = true
                    },
                ) { Text("恢复自动", fontSize = 12.sp) }
            }
        }

        // ── Starcloud ID（弹窗登录，凭证不回显） ──
        SectionCard("Starcloud ID") {
            var loggedIn by remember { mutableStateOf(LauncherPrefs.starcloudLoggedIn(context)) }
            var showLogin by remember { mutableStateOf(false) }

            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column {
                    Text(
                        if (loggedIn) "已登录" else "未登录",
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                    )
                    Text(
                        if (loggedIn) "uid：" + LauncherPrefs.starcloudUid(context)
                        else "登录后可被电脑端调用并使用在线能力",
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                    )
                }
                Button(
                    onClick = { showLogin = true },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = FluentColors.Accent,
                        contentColor = FluentColors.OnAccent,
                    ),
                ) { Text(if (loggedIn) "重新登录" else "登录") }
            }
            if (loggedIn) {
                TextButton(onClick = {
                    LauncherPrefs.starcloudLogout(context)
                    cn.starcloud.launcher.compose.game.AgentService.stop()
                    loggedIn = false
                }) { Text("退出登录", fontSize = 12.sp, color = FluentColors.Danger) }
            }

            if (showLogin) {
                StarcloudLoginDialog(
                    context = context,
                    onDismiss = { showLogin = false },
                    onLoggedIn = {
                        loggedIn = true
                        // Agent 若开着，自动用新凭证重启
                        if (LauncherPrefs.agentEnabled(context)) {
                            cn.starcloud.launcher.compose.game.AgentService.stop()
                            cn.starcloud.launcher.compose.game.AgentService.start(context)
                        }
                    },
                )
            }
        }

        // ── 触摸控制（游戏内键位层） ──
        SectionCard("触摸控制") {
            var touchOn by remember { mutableStateOf(LauncherPrefs.touchControlsEnabled(context)) }
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("游戏内触摸控件（虚拟按键/摇杆）", fontSize = 14.sp)
                Switch(
                    checked = touchOn,
                    onCheckedChange = {
                        touchOn = it
                        LauncherPrefs.setTouchControlsEnabled(context, it)
                    },
                )
            }
            Text(
                "启动游戏后显示虚拟按键与摇杆；悬浮球可切换编辑/操作、保存与切换布局。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
            )
            OutlinedButton(
                onClick = {
                    context.startActivity(
                        android.content.Intent(context, cn.starcloud.launcher.compose.game.TouchLayoutActivity::class.java)
                            .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK),
                    )
                },
            ) { Text("键位布局编辑器", fontSize = 13.sp) }
        }

        // ── 跨设备 Agent ──
        SectionCard("跨设备 Agent") {
            var agentOn by remember { mutableStateOf(LauncherPrefs.agentEnabled(context)) }
            var agentName by remember { mutableStateOf(LauncherPrefs.agentDeviceName(context)) }
            var agentError by remember { mutableStateOf<String?>(null) }

            fun saveAndRestart(turnOn: Boolean) {
                LauncherPrefs.setAgentEnabled(context, turnOn)
                agentOn = turnOn
                if (turnOn) cn.starcloud.launcher.compose.game.AgentService.start(context)
                else cn.starcloud.launcher.compose.game.AgentService.stop()
            }

            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("供电脑端调用（同一局域网）", fontSize = 14.sp)
                Switch(
                    checked = agentOn,
                    onCheckedChange = { on ->
                        if (on && !LauncherPrefs.starcloudLoggedIn(context)) {
                            agentError = "请先在上方登录 Starcloud ID"
                            return@Switch
                        }
                        agentError = null
                        saveAndRestart(on)
                    },
                )
            }
            if (!LauncherPrefs.starcloudLoggedIn(context)) {
                Text(
                    "请先在上方「Starcloud ID」登录；登录后 Agent 将使用同一份凭证。",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                )
            }
            OutlinedTextField(
                value = agentName,
                onValueChange = { agentName = it; LauncherPrefs.setAgentDeviceName(context, it) },
                label = { Text("设备名（电脑端看到的名称）") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            agentError?.let {
                Text(it, fontSize = 12.sp, color = FluentColors.Danger)
            }
            Text(
                "开启后手机会出现在电脑端的「跨设备」列表里；电脑端可经局域网调用本机的存档同步等能力。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
            )
        }

        // ── 关于 ──
        SectionCard("关于") {
            InfoRow("产品", "Starcloud Minecraft Launcher（安卓 · Compose）")
            InfoRow("版本", "V0.3.7 测试版")
            InfoRow("数据目录", context.getExternalFilesDir(null)?.path ?: context.filesDir.path)
        }
    }
}

@Composable
private fun SectionCard(title: String, content: @Composable () -> Unit) {
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(),
        shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
    ) {
        Column(
            Modifier.fillMaxWidth().padding(FluentDimens.CardPadding),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.Medium)
            content()
        }
    }
}

@Composable
private fun ThemeOption(label: String, selected: Boolean, onClick: () -> Unit) {
    val colors = if (selected) {
        CardDefaults.cardColors(
            containerColor = FluentColors.Accent,
            contentColor = FluentColors.OnAccent,
        )
    } else {
        CardDefaults.cardColors()
    }
    Card(
        colors = colors,
        shape = RoundedCornerShape(FluentDimens.CornerRadius),
        onClick = onClick,
    ) {
        Text(
            label,
            fontSize = 13.sp,
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp),
        )
    }
}

@Composable
private fun InfoRow(label: String, value: String) {
    Column {
        Text(label, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f))
        Text(value, fontSize = 13.sp)
    }
}
