// 下载页 · Win 端复刻（缩小版）：左侧「原版游戏 + 社区资源」分区栏 + 右侧版本列表，
// 顶部选中版本行、类型筛选、底部通栏「开始下载」。手机横屏下按 Win 布局等比缩小。
// 社区资源（Mod / 整合包 / 数据包 / 资源包 / 光影包）分区随后续里程碑接 Modrinth / CurseForge。
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
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
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.InstallState
import cn.starcloud.launcher.compose.data.ManifestEntry
import cn.starcloud.launcher.compose.data.ManifestRepository
import cn.starcloud.launcher.compose.data.VersionIcons
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import cn.starcloud.launcher.compose.ui.theme.HarmonyIconFont
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.time.OffsetDateTime

internal fun fmtDate(iso: String): String = try {
    OffsetDateTime.parse(iso).toLocalDate().toString().replace('-', '/')
} catch (e: Exception) {
    iso
}

private data class SideItem(val key: String, val label: String, val glyph: String)

// 图标码位与 Win 端 DownloadPage 分区栏一致（HarmonyOS 图标字体）
private val SIDE_ITEMS = listOf(
    SideItem("vanilla", "原版游戏", "\uEC20"),
    SideItem("mod", "Mod", "\uEA93"),
    SideItem("modpack", "整合包", "\uECAF"),
    SideItem("datapack", "数据包", "\uEC24"),
    SideItem("resourcepack", "资源包", "\uEC49"),
    SideItem("shaderpack", "光影包", "\uEC0C"),
)

private fun typeLabel(type: String) = when (type) {
    "release" -> "正式版"
    "snapshot" -> "快照版"
    "old_beta" -> "远古版 Beta"
    "old_alpha" -> "远古版 Alpha"
    else -> type
}

private fun typeColor(type: String) = when (type) {
    "release" -> Color(0xFF7CB342)
    "snapshot" -> Color(0xFF9575CD)
    else -> Color(0xFFA1887F)
}

@Composable
fun DownloadScreen(
    cacheFile: File,
    filesRoot: File,
    refreshKey: Int,
    onStartDownload: (ManifestEntry) -> Unit = {},
) {
    val repo = remember(cacheFile) { ManifestRepository(cacheFile) }
    var entries by remember(cacheFile) { mutableStateOf<List<ManifestEntry>?>(null) }
    var error by remember(cacheFile) { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    var selectedId by remember { mutableStateOf<String?>(null) }
    var sideSelected by remember { mutableStateOf("vanilla") }
    var typeFilter by remember { mutableStateOf("全部") }

    val installing = InstallState.version
    val stage = InstallState.stage
    val file = InstallState.file
    val done = InstallState.done
    val total = InstallState.total
    val message = InstallState.message

    LaunchedEffect(refreshKey, reload) {
        error = null
        repo.cached()?.let { if (entries == null) entries = it }
        try {
            entries = withContext(Dispatchers.IO) { repo.fetch() }
        } catch (e: Exception) {
            if (entries == null) error = e.message ?: e.javaClass.simpleName
        }
    }

    val all = entries ?: emptyList()
    val filtered = when (typeFilter) {
        "正式版" -> all.filter { it.type == "release" }
        "快照版" -> all.filter { it.type == "snapshot" }
        "远古版" -> all.filter { it.type == "old_beta" || it.type == "old_alpha" }
        else -> all
    }
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val selectedEntry = all.firstOrNull { it.id == selectedId }

    Row(Modifier.fillMaxSize().padding(10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {

        // ── 左：分区栏（原版游戏 + 社区资源） ──
        Card(
            Modifier.width(150.dp).fillMaxHeight(),
            colors = CardDefaults.cardColors(),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
        ) {
            Column(Modifier.fillMaxWidth().padding(8.dp)) {
                SideRow(
                    item = SIDE_ITEMS[0],
                    selected = sideSelected == "vanilla",
                    onClick = {
                        sideSelected = "vanilla"
                        typeFilter = "全部"
                    },
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    "社区资源",
                    fontSize = 10.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                    modifier = Modifier.padding(start = 10.dp, top = 2.dp, bottom = 2.dp),
                )
                SIDE_ITEMS.drop(1).forEach { item ->
                    SideRow(
                        item = item,
                        selected = sideSelected == item.key,
                        onClick = { sideSelected = item.key },
                    )
                }
                if (sideSelected != "vanilla") {
                    Text(
                        "该分区随后续版本交付，敬请期待。",
                        fontSize = 10.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                        modifier = Modifier.padding(10.dp),
                    )
                }
            }
        }

        // ── 右：选中版本行 + 类型筛选 + 全部版本列表 + 开始下载 ──
        Column(
            Modifier.weight(1f).fillMaxHeight(),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            if (sideSelected != "vanilla") {
                Card(
                    Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(),
                    shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                ) {
                    Column(Modifier.fillMaxWidth().padding(FluentDimens.CardPadding)) {
                        Text(
                            SIDE_ITEMS.firstOrNull { it.key == sideSelected }?.label ?: "",
                            fontSize = 15.sp,
                            fontWeight = FontWeight.Medium,
                        )
                        Spacer(Modifier.height(6.dp))
                        Text(
                            "社区资源下载随后续版本交付。",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                        )
                    }
                }
            } else {
                // 选中版本行（对齐 Win 端顶部选中行）
                selectedEntry?.let { e ->
                    Card(
                        Modifier.fillMaxWidth(),
                        colors = CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
                        ),
                        shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                    ) {
                        Row(
                            Modifier.fillMaxWidth().padding(10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            val icon = VersionIcons.bitmap(ctx, VersionIcons.fileFor(e.type, e.id))
                            if (icon != null) {
                                Image(
                                    icon.asImageBitmap(),
                                    contentDescription = e.id,
                                    modifier = Modifier.size(30.dp),
                                )
                            } else {
                                Box(
                                    Modifier.size(30.dp).background(typeColor(e.type), RoundedCornerShape(6.dp)),
                                )
                            }
                            Text(e.id, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                            Text(
                                "· " + typeLabel(e.type),
                                fontSize = 12.sp,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                            )
                        }
                    }
                }

                // 类型筛选（对齐 Win 端类别筛选）
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    listOf("全部", "正式版", "快照版", "远古版").forEach { f ->
                        val selected = typeFilter == f
                        Text(
                            f,
                            fontSize = 11.sp,
                            color = if (selected) FluentColors.OnAccent
                            else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                            modifier = Modifier
                                .background(
                                    if (selected) FluentColors.Accent
                                    else MaterialTheme.colorScheme.surfaceContainer,
                                    RoundedCornerShape(FluentDimens.CornerRadius),
                                )
                                .clickable { typeFilter = f }
                                .padding(horizontal = 10.dp, vertical = 4.dp),
                        )
                    }
                }

                // 全部版本列表
                Card(
                    Modifier.weight(1f).fillMaxWidth(),
                    colors = CardDefaults.cardColors(),
                    shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                ) {
                    Column(Modifier.fillMaxSize().padding(8.dp)) {
                        Text(
                            "全部版本 (${filtered.size})",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f),
                        )
                        Spacer(Modifier.height(4.dp))
                        LazyColumn(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            items(filtered, key = { it.id }) { e ->
                                val isSelected = e.id == selectedId
                                Row(
                                    Modifier
                                        .fillMaxWidth()
                                        .background(
                                            if (isSelected) MaterialTheme.colorScheme.surfaceContainerHighest
                                            else Color.Transparent,
                                            RoundedCornerShape(FluentDimens.CornerRadius),
                                        )
                                        .clickable {
                                            selectedId = e.id
                                            // 点击版本直接进入「安装模组加载器」二级页
                                            onStartDownload(e)
                                        }
                                        .padding(horizontal = 6.dp, vertical = 5.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                ) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        val icon = VersionIcons.bitmap(ctx, VersionIcons.fileFor(e.type, e.id))
                                        if (icon != null) {
                                            Image(
                                                icon.asImageBitmap(),
                                                contentDescription = e.id,
                                                modifier = Modifier.size(22.dp),
                                            )
                                        } else {
                                            Box(
                                                Modifier.size(22.dp)
                                                    .background(typeColor(e.type), RoundedCornerShape(5.dp)),
                                            )
                                        }
                                        Spacer(Modifier.width(8.dp))
                                        Text(e.id, fontSize = 13.sp, fontWeight = FontWeight.Medium)
                                    }
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Text(
                                            "· " + typeLabel(e.type),
                                            fontSize = 10.sp,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                                        )
                                        if (isSelected) {
                                            Spacer(Modifier.width(8.dp))
                                            Text(
                                                "已选",
                                                fontSize = 10.sp,
                                                color = FluentColors.OnAccent,
                                                modifier = Modifier
                                                    .background(FluentColors.Accent, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                                                    .padding(horizontal = 6.dp, vertical = 2.dp),
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 安装进度卡
                if (installing != null || message != null) {
                    Card(
                        Modifier.fillMaxWidth(),
                        colors = CardDefaults.cardColors(),
                        shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
                    ) {
                        Column(
                            Modifier.fillMaxWidth().padding(FluentDimens.CardPadding),
                            verticalArrangement = Arrangement.spacedBy(4.dp),
                        ) {
                            Text(installing?.let { "正在安装 $it" } ?: (message ?: ""), fontSize = 12.sp)
                            if (installing != null) {
                                LinearProgressIndicator(
                                    progress = { if (total > 0) done.toFloat() / total else 0f },
                                    modifier = Modifier.fillMaxWidth(),
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SideRow(item: SideItem, selected: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .background(
                if (selected) MaterialTheme.colorScheme.surfaceContainerHigh else Color.Transparent,
                RoundedCornerShape(FluentDimens.CornerRadius),
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 6.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(
            item.glyph,
            fontFamily = HarmonyIconFont,
            fontSize = 18.sp,
            color = if (selected) FluentColors.Accent
            else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
        )
        Text(
            item.label,
            fontSize = 13.sp,
            fontWeight = if (selected) FontWeight.Medium else FontWeight.Normal,
            color = if (selected) FluentColors.Accent else MaterialTheme.colorScheme.onSurface,
        )
    }
}
