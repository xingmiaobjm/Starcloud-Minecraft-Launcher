// M1 版本页：已安装版本列表 + 删除（对齐 Uno 版本页语汇的最小可用版）
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
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
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.InstalledVersion
import cn.starcloud.launcher.compose.data.VersionsRepository
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
    var versions by remember(refreshKey) { mutableStateOf(repo.list()) }
    var pendingDelete by remember { mutableStateOf<InstalledVersion?>(null) }

    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        PageHeader(title = "版本管理", actionLabel = "刷新", onAction = { versions = repo.list() })

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
                            Column {
                                Text(v.id, fontSize = 14.sp)
                                Text(
                                    v.type + " · " + fmtSize(v.sizeBytes) + " · " +
                                        SimpleDateFormat("yyyy/MM/dd HH:mm", Locale.getDefault()).format(Date(v.lastModified)),
                                    fontSize = 11.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                )
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
                    repo.remove(v.id)
                    versions = repo.list()
                    pendingDelete = null
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { pendingDelete = null }) { Text("取消") } },
        )
    }
}

