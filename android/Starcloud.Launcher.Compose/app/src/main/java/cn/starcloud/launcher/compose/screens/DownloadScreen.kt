// M2 下载页：版本清单列表 + 点击安装 + 实时进度（安装进行中跨页面存活，见 InstallState）
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.AppScope
import cn.starcloud.launcher.compose.data.InstallState
import cn.starcloud.launcher.compose.data.ManifestEntry
import cn.starcloud.launcher.compose.data.ManifestRepository
import cn.starcloud.launcher.compose.data.VersionDownloader
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.time.OffsetDateTime

/** 清单 ISO 时间 → yyyy/MM/dd（解析失败回退原文） */
private fun fmtDate(iso: String): String = try {
    OffsetDateTime.parse(iso).toLocalDate().toString().replace('-', '/')
} catch (e: Exception) {
    iso
}

@Composable
fun DownloadScreen(cacheFile: File, filesRoot: File, refreshKey: Int) {
    val repo = remember(cacheFile) { ManifestRepository(cacheFile) }
    var entries by remember(cacheFile) { mutableStateOf<List<ManifestEntry>?>(null) }
    var error by remember(cacheFile) { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }

    // 全局安装状态（跨页面存活）；离开页面后下载继续。
    // 直接读快照属性（object 上的委托属性再用 by 转委托会触发 KProperty0 解析问题）
    val installing = InstallState.version
    val stage = InstallState.stage
    val file = InstallState.file
    val done = InstallState.done
    val total = InstallState.total
    val message = InstallState.message

    LaunchedEffect(refreshKey, reload) {
        error = null
        // 先上屏缓存（秒开），再网络刷新覆盖；失败且无缓存才报错
        repo.cached()?.let { if (entries == null) entries = it }
        try {
            entries = withContext(Dispatchers.IO) { repo.fetch() }
        } catch (e: Exception) {
            if (entries == null) error = e.message ?: e.javaClass.simpleName
        }
    }

    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        PageHeader(title = "下载", actionLabel = "刷新", onAction = { reload++ })

        // 安装进度卡（进行中/刚结束都显示）
        if (installing != null || message != null) {
            Card(
                    Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(),
                    shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                ) {
                Column(Modifier.fillMaxWidth().padding(FluentDimens.CardPadding), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(
                        (installing?.let { "正在安装 $it" } ?: "") + (message ?: ""),
                        fontSize = 13.sp,
                    )
                    if (installing != null) {
                        Text(stage + "　" + file, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f))
                        if (total > 0) {
                            LinearProgressIndicator(
                                progress = { done.toFloat() / total },
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Text("$done / $total", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f))
                        }
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            TextButton(onClick = { InstallState.cancel() }) { Text("取消") }
                        }
                    } else {
                        // 失败态：错误行 + 首错样例（诊断镜像可达性）
                        if (file.startsWith("失败样例："))
                            Text(file, fontSize = 10.sp, color = MaterialTheme.colorScheme.error)
                        if (message?.startsWith("安装完成") == true)
                            Text("到「版本」页查看已装列表", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f))
                    }
                }
            }
        }

        when {
            entries == null && error == null ->
                Text("清单加载中…", color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f))
            error != null ->
                Text("清单拉取失败：" + error, color = MaterialTheme.colorScheme.error)
            else -> LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                items(entries ?: emptyList(), key = { it.id }) { e ->
                    // 手机版：安装入口收敛为显式按钮（整卡可点曾导致热区歧义）
                    Card(
                    Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(),
                    shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                ) {
                        Row(Modifier.fillMaxWidth().padding(FluentDimens.CardPadding), horizontalArrangement = Arrangement.SpaceBetween) {
                            Column {
                                Text(e.id + (if (installing == e.id) "（安装中）" else ""), fontSize = 14.sp)
                                Text(
                                    fmtDate(e.releaseTime),
                                    fontSize = 11.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                )
                            }
                            Column(horizontalAlignment = Alignment.End) {
                                Text(e.type, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f))
                                TextButton(
                                    enabled = installing == null,
                                    onClick = {
                                        InstallState.version = e.id
                                        val downloader = VersionDownloader(filesRoot)
                                        AppScope.scope.launch {
                                            withContext(Dispatchers.IO) { downloader.downloadVersion(e) }
                                        }
                                    },
                                ) { Text("安装", fontSize = 12.sp) }
                            }
                        }
                    }
                }
            }
        }
    }
}
