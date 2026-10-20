// 首页 · Win 端复刻：双列卡片布局（左：启动自检 + 已安装版本库；右：启动卡）。
// 启动链（M3）交付前，启动卡显示版本探针与导航入口；数据来自 VersionsRepository（真实扫描）。
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.InstalledVersion
import cn.starcloud.launcher.compose.data.VersionsRepository
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

private fun fmtSize(bytes: Long): String = when {
    bytes <= 0L -> "未完成"
    bytes < 1024L * 1024 -> String.format(Locale.US, "%d KB", bytes / 1024)
    bytes < 1024L * 1024 * 1024 -> String.format(Locale.US, "%.1f MB", bytes / 1048576.0)
    else -> String.format(Locale.US, "%.2f GB", bytes / 1073741824.0)
}

@Composable
fun HomeScreen(
    filesRoot: File,
    probeVersions: () -> String,
    probeStorage: () -> String,
    onNavigate: (String) -> Unit,
    onLaunchVersion: (String) -> Unit,
    launchStatus: String?,
    launchBusy: Boolean,
) {
    val repo = remember(filesRoot) { VersionsRepository(filesRoot) }
    var versions by remember(filesRoot) { mutableStateOf(repo.list()) }
    var refreshKey by remember { mutableIntStateOf(0) }
    var selectedId by remember { mutableStateOf<String?>(null) }

    // 刷新键变化或进入页面时重扫
    androidx.compose.runtime.LaunchedEffect(refreshKey) { versions = repo.list() }

    val selected = versions.firstOrNull { it.id == selectedId } ?: versions.firstOrNull()

    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {

        // ── 启动自检行（对齐 Win 端自检卡：版本/存储 就绪态） ──
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            CheckChip(label = "游戏版本", value = probeVersions())
            CheckChip(label = "存储", value = probeStorage())
        }

        // ── 双列主体：左 = 版本库卡（撑满）；右 = 启动卡（固定宽，对齐 Win 端右栏启动卡） ──
        Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {

            // 左：版本库卡
            Card(
                Modifier.weight(1f).fillMaxHeight(),
                colors = CardDefaults.cardColors(),
                shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
            ) {
                Column(Modifier.fillMaxSize().padding(FluentDimens.CardPadding)) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                        Text("已安装版本 (${versions.size})", fontSize = 15.sp, fontWeight = FontWeight.Medium)
                        TextButton(onClick = { refreshKey++ }) { Text("刷新") }
                    }
                    LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        items(versions, key = { it.id }) { v ->
                            val isSelected = v.id == (selected?.id ?: "")
                            Card(
                                Modifier.fillMaxWidth(),
                                colors = CardDefaults.cardColors(
                                    containerColor = if (isSelected) MaterialTheme.colorScheme.surfaceContainerHighest
                                    else MaterialTheme.colorScheme.surfaceContainer,
                                ),
                                shape = RoundedCornerShape(FluentDimens.CornerRadius),
                            ) {
                                Row(
                                    Modifier.fillMaxWidth().padding(12.dp),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Column {
                                        Text(v.id, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                                        Text(
                                            v.type + " · " + fmtSize(v.sizeBytes),
                                            fontSize = 11.sp,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                        )
                                    }
                                    Text(
                                        SimpleDateFormat("yyyy/MM/dd", Locale.getDefault()).format(Date(v.lastModified)),
                                        fontSize = 11.sp,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                    )
                                }
                            }
                        }
                    }
                }
            }

            // 右：启动卡（对齐 Win 端右栏账户/启动卡）
            Card(
                Modifier.width(240.dp).fillMaxHeight(),
                colors = CardDefaults.cardColors(),
                shape = RoundedCornerShape(FluentDimens.CornerRadiusLarge),
            ) {
                Column(
                    Modifier.fillMaxSize().padding(FluentDimens.CardPadding),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Text("启动", fontSize = 15.sp, fontWeight = FontWeight.Medium)
                    Spacer(Modifier.weight(1f))
                    Button(
                        onClick = {
                            val id = selected?.id
                            if (id != null) onLaunchVersion(id) else onNavigate("download")
                        },
                        enabled = !launchBusy,
                        modifier = Modifier.fillMaxWidth(),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = FluentColors.Accent,
                            contentColor = FluentColors.OnAccent,
                        ),
                    ) {
                        Text(
                            if (launchBusy) "启动中…" else "启动游戏",
                            fontWeight = FontWeight.Medium,
                        )
                    }
                    Text(
                        selected?.id ?: "未选择版本",
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                        modifier = Modifier.align(Alignment.CenterHorizontally),
                    )
                    launchStatus?.let {
                        Text(
                            it,
                            fontSize = 10.sp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun CheckChip(label: String, value: String) {
    Card(
        colors = CardDefaults.cardColors(),
        shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
    ) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(label + "：", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f))
            Text(value, fontSize = 12.sp, fontWeight = FontWeight.Medium)
        }
    }
}
