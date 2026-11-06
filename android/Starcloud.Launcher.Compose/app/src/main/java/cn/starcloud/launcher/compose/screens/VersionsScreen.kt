// M1 版本页：已安装版本列表 + 删除（对齐 Uno 版本页语汇的最小可用版）
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.InstalledVersion
import cn.starcloud.launcher.compose.data.VersionIcons
import cn.starcloud.launcher.compose.data.VersionsRepository
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
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
fun VersionsScreen(repo: VersionsRepository, refreshKey: Int) {
    // 扫盘在主线程会卡首帧（每个版本 JSON 都要读一遍）：改成异步加载
    val scope = rememberCoroutineScope()
    var versions by remember(refreshKey) { mutableStateOf(emptyList<InstalledVersion>()) }
    var pendingDelete by remember { mutableStateOf<InstalledVersion?>(null) }
    var deleteError by remember { mutableStateOf<String?>(null) }

    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        deleteError?.let { msg ->
            Text(msg, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
        }
        PageHeader(title = "版本管理", actionLabel = "刷新", onAction = { scope.launch { versions = withContext(Dispatchers.IO) { repo.list() } } })

        if (versions.isEmpty()) {
            Column(
                Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text("暂无已安装版本", color = MaterialTheme.colorScheme.primary)
                Text(
                    "去「下载」页安装一个吧",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                )
            }
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                items(versions, key = { it.id }) { v ->
                    Card(
                        Modifier.fillMaxWidth(),
                        colors = CardDefaults.cardColors(),
                        shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                    ) {
                        Row(
                            Modifier.fillMaxWidth().padding(FluentDimens.CardPadding),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                val icon = VersionIcons.bitmap(
                                    LocalContext.current,
                                    VersionIcons.fileFor(v.type, v.id, v.loader ?: v.type),
                                )
                                if (icon != null) {
                                    Image(
                                        icon.asImageBitmap(),
                                        contentDescription = v.id,
                                        modifier = Modifier.padding(end = 8.dp).size(28.dp),
                                    )
                                }
                                Column {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Text(v.id, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                                        if (v.loader != null) {
                                            Text(
                                                " · " + v.loader!!.replaceFirstChar { it.uppercase() },
                                                fontSize = 11.sp,
                                                color = FluentColors.Accent,
                                                modifier = Modifier.padding(start = 6.dp),
                                            )
                                        }
                                    }
                                    Text(
                                        v.type + " · " + fmtSize(v.sizeBytes) + " · " +
                                            SimpleDateFormat("yyyy/MM/dd HH:mm", Locale.getDefault()).format(Date(v.lastModified)),
                                        fontSize = 11.sp,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                    )
                                }
                            }
                            TextButton(onClick = { pendingDelete = v }) {
                                Text("删除", color = MaterialTheme.colorScheme.error)
                            }
                        }
                    }
                }
            }
        }
    }

    pendingDelete?.let { v ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("删除版本") },
            text = { Text("确定要删除版本 " + v.id + " 吗？将删除其版本文件夹，此操作不可恢复。") },
            confirmButton = {
                TextButton(onClick = {
                    // 删除是主线程的递归删（可能很慢），且失败不能静默：
                    // 原来既没切到后台也没 try/catch，权限不足时直接崩。
                    scope.launch {
                        val failure = withContext(Dispatchers.IO) {
                            runCatching { repo.remove(v.id) }.exceptionOrNull()
                        }
                        if (failure == null) {
                            versions = withContext(Dispatchers.IO) { repo.list() }
                            pendingDelete = null
                        } else {
                            pendingDelete = null
                            deleteError = failure.message ?: "删除失败"
                        }
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { pendingDelete = null }) { Text("取消") } },
        )
    }
}

