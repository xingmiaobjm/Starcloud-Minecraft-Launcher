// 版本安装二级页（对齐 Win 端「安装模组加载器」视图）：
//   顶部 = 版本头（返回 + 方块图标 + id + 日期 · 类型）
//   加载器卡 = Forge / NeoForge / Fabric / Quilt，点展开显示构建列表（最新/推荐标记 + 已选徽标）；
//              OptiFine 暂不支持（置灰）
//   底部 = 版本名称输入（新建独立实例）+ 已安装份数提示 + 开始下载
// 流程：先下载原版本体 → Fabric/Quilt 再合并加载器 profile 并下载 Maven 库；
//       Forge/NeoForge 的官方安装器随后续版本移植，选中时只装原版并提示。
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.FabricLoaderInstaller
import cn.starcloud.launcher.compose.data.InstallState
import cn.starcloud.launcher.compose.data.ManifestEntry
import cn.starcloud.launcher.compose.data.VersionDownloader
import cn.starcloud.launcher.compose.data.VersionIcons
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import cn.starcloud.launcher.compose.ui.theme.HarmonyIconFont
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.time.OffsetDateTime

internal fun fmtDateOf(iso: String): String = try {
    OffsetDateTime.parse(iso).toLocalDate().toString().replace('-', '/')
} catch (e: Exception) {
    iso
}

private data class LoaderSpec(
    val key: String, val label: String, val supported: Boolean, val metaBase: String? = null,
)

private val LOADERS = listOf(
    LoaderSpec("forge", "Forge", true, "https://files.minecraftforge.net/net/minecraftforge/forge"),
    LoaderSpec("neoforge", "NeoForge", true, "https://maven.neoforged.net/releases/net/neoforged/neoforge"),
    LoaderSpec("fabric", "Fabric", true, "https://meta.fabricmc.net/v2/versions/loader"),
    LoaderSpec("quilt", "Quilt", true, "https://meta.quiltmc.org/v3/versions/loader"),
    LoaderSpec("optifine", "OptiFine", false),
)

@Composable
fun LoaderSelectScreen(
    entry: ManifestEntry,
    filesRoot: File,
    onDone: () -> Unit,
    onBack: () -> Unit,
) {
    val ctx = LocalContext.current
    var expanded by remember { mutableStateOf<String?>(null) }
    // 每个加载器独立状态：key 不存在 = 获取中；null = 获取失败；空列表 = 无构建
    var builds by remember { mutableStateOf<Map<String, List<String>?>>(emptyMap()) }
    val picked = remember { mutableStateMapOf<String, String>() }
    var selectedLoader by remember { mutableStateOf<String?>(null) }
    var nameText by remember { mutableStateOf(entry.id) }
    var busy by remember { mutableStateOf(false) }
    var stage by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    // 打开时并行拉取四个加载器的构建列表（每个独立完成，慢源/坏源不拖累其他卡）
    LaunchedEffect(entry.id) {
        LOADERS.filter { it.supported }.forEach { spec ->
            launch {
                try {
                    val list = withContext(Dispatchers.IO) {
                        LoaderBuildsFetcher.fetch(spec.key, spec.metaBase!!, entry.id)
                    }
                    builds = builds + (spec.key to list)
                } catch (e: Exception) {
                    builds = builds + (spec.key to null)
                }
            }
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .verticalScroll(rememberScrollState())
            .padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        // ── 版本头 ──
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
                TextButton(
                    onClick = onBack,
                    modifier = Modifier.size(width = 44.dp, height = 44.dp),
                ) { Text("←", fontSize = 18.sp) }

                val icon = VersionIcons.bitmap(ctx, VersionIcons.fileFor(entry.type, entry.id))
                if (icon != null) {
                    Image(
                        icon.asImageBitmap(),
                        contentDescription = entry.id,
                        modifier = Modifier.size(34.dp),
                    )
                }
                Column {
                    Text(entry.id, fontSize = 16.sp, fontWeight = FontWeight.Medium)
                    Text(
                        fmtDateOf(entry.releaseTime) + " · " + typeLabelOf(entry.type),
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                    )
                }
            }
        }

        // ── 加载器卡（Forge / NeoForge / Fabric / Quilt 可展开，OptiFine 置灰） ──
        LOADERS.forEach { spec ->
            val expandedHere = expanded == spec.key
            val loaded = builds.containsKey(spec.key)
            val failed = loaded && builds[spec.key] == null
            val list = builds[spec.key].orEmpty()
            val pickedBuild = picked[spec.key]
            Card(
                Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(
                    containerColor = if (expandedHere) MaterialTheme.colorScheme.surfaceContainerHigh
                    else MaterialTheme.colorScheme.surfaceContainer,
                ),
                shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
            ) {
                Column(Modifier.fillMaxWidth()) {
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable(enabled = spec.supported) {
                                expanded = if (expandedHere) null else spec.key
                            }
                            .padding(horizontal = 14.dp, vertical = 12.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            if (spec.supported) {
                                val loaderIcon = VersionIcons.bitmap(ctx, "${spec.key}.png")
                                if (loaderIcon != null) {
                                    Image(
                                        loaderIcon.asImageBitmap(),
                                        contentDescription = spec.label,
                                        modifier = Modifier.size(24.dp),
                                    )
                                    Spacer(Modifier.width(10.dp))
                                }
                            }
                            Text(spec.label, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                            Spacer(Modifier.width(8.dp))
                            val statusText = when {
                                !spec.supported -> "暂不支持"
                                pickedBuild != null -> "已选 $pickedBuild"
                                !loaded -> "获取中…"
                                failed -> "获取失败"
                                list.isEmpty() -> "可以添加"
                                else -> "${list.size} 个构建"
                            }
                            Text(
                                statusText,
                                fontSize = 12.sp,
                                color = if (pickedBuild != null) FluentColors.Accent
                                else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                            )
                        }
                        if (spec.supported) {
                            Text("⌄", fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f))
                        }
                    }

                    if (expandedHere && spec.supported) {
                        Column(
                            Modifier.fillMaxWidth().padding(horizontal = 10.dp).padding(bottom = 10.dp),
                        ) {
                            if (!loaded) {
                                Text(
                                    "获取中…",
                                    fontSize = 12.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                )
                            } else if (failed) {
                                Text(
                                    "获取失败，请稍后重试",
                                    fontSize = 12.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                )
                            } else if (list.isEmpty()) {
                                Text(
                                    "暂无可用构建",
                                    fontSize = 12.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                )
                            } else {
                                // 只展示最新 10 个构建（完整列表在 Win 端查看）
                                list.take(10).forEachIndexed { index, build ->
                                    val isPicked = pickedBuild == build
                                    Row(
                                        Modifier
                                            .fillMaxWidth()
                                            .background(
                                                if (isPicked) MaterialTheme.colorScheme.surfaceContainerHighest
                                                else Color.Transparent,
                                                RoundedCornerShape(FluentDimens.CornerRadius),
                                            )
                                            .clickable {
                                                picked.clear()
                                                picked[spec.key] = build
                                                selectedLoader = spec.key
                                            }
                                            .padding(horizontal = 6.dp, vertical = 6.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                        verticalAlignment = Alignment.CenterVertically,
                                    ) {
                                        Text(build, fontSize = 13.sp, fontWeight = FontWeight.Medium)
                                        val tag = when {
                                            index == 0 -> "最新"
                                            index == 1 && list.size > 2 -> "推荐"
                                            else -> null
                                        }
                                        if (tag != null) {
                                            Text(
                                                tag,
                                                fontSize = 10.sp,
                                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                                modifier = Modifier
                                                    .background(
                                                        MaterialTheme.colorScheme.surfaceContainerHighest,
                                                        RoundedCornerShape(FluentDimens.CornerRadiusSmall),
                                                    )
                                                    .padding(horizontal = 6.dp, vertical = 2.dp),
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // ── 版本名称（新建独立实例） ──
        Card(
            Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
        ) {
            Column(
                Modifier.fillMaxWidth().padding(FluentDimens.CardPadding),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text("版本名称（新建独立实例）", fontSize = 14.sp, fontWeight = FontWeight.Medium)
                OutlinedTextField(
                    value = nameText,
                    onValueChange = { nameText = it },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                    modifier = Modifier.fillMaxWidth(),
                )
                Text(
                    "内容将单独存放在 versions/<版本名称>/ 下，不影响其他实例。",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                )
            }
        }

        // ── 提示 / 错误 / 阶段 ──
        error?.let { Text(it, fontSize = 12.sp, color = FluentColors.Danger) }
        stage?.let { Text(it, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f)) }

        // ── 开始下载 ──
        Button(
            onClick = {
                val name = nameText.trim()
                if (name.isBlank()) {
                    error = "请填写版本名称"
                    return@Button
                }
                busy = true
                error = null
                val scope = cn.starcloud.launcher.compose.data.AppScope.scope
                scope.launch {
                    var loaderInheritId: String? = null
                    try {
                        withContext(Dispatchers.IO) {
                            InstallState.version = entry.id

                            // ① 原版本体
                            stage = "下载原版 ${entry.id}…"
                            cn.starcloud.launcher.compose.data.VersionDownloader(filesRoot)
                                .downloadVersion(entry)

                            // ② Fabric / Quilt 加载器（profile 合并 + Maven 库）
                            val loaderSpec = LOADERS.firstOrNull { it.key == selectedLoader }
                            if (loaderSpec != null && loaderSpec.supported &&
                                loaderSpec.key in listOf("fabric", "quilt")
                            ) {
                                val build = picked[loaderSpec.key]
                                    ?: throw IllegalStateException("请先选择 ${loaderSpec.label} 的构建版本")
                                stage = "安装 ${loaderSpec.label} $build…"
                                loaderInheritId = FabricLoaderInstaller.install(
                                    ctx, filesRoot, entry.id, build,
                                    quilt = loaderSpec.key == "quilt",
                                    targetId = "$name-loader",
                                    onStage = { s -> stage = s },
                                )
                            } else if (loaderSpec != null && loaderSpec.key in listOf("forge", "neoforge")) {
                                throw IllegalStateException(
                                    "${loaderSpec.label} 的安装器随后续版本支持，本次已先下载原版",
                                )
                            }
                        }

                        // ③ 版本骨架：继承加载器（或原版），命名为用户输入
                        withContext(Dispatchers.Main) {
                            val inheritId = loaderInheritId ?: entry.id
                            val dir = File(filesRoot, ".minecraft/versions").resolve(name)
                            dir.mkdirs()
                            File(dir, "$name.json").writeText(
                                """{"id":"$name","inheritsFrom":"$inheritId","type":"release"}""",
                            )
                            busy = false
                            onDone()
                        }
                    } catch (e: Exception) {
                        withContext(Dispatchers.Main) {
                            busy = false
                            error = e.message ?: e.javaClass.simpleName
                        }
                    }
                }
            },
            enabled = !busy,
            modifier = Modifier.fillMaxWidth().height(44.dp),
            colors = ButtonDefaults.buttonColors(
                containerColor = FluentColors.Accent,
                contentColor = FluentColors.OnAccent,
            ),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusLarge),
        ) {
            Text(
                if (busy) "下载中…" else "⤓ 开始下载",
                fontSize = 15.sp,
                fontWeight = FontWeight.Medium,
            )
        }
    }
}

private fun typeLabelOf(type: String) = when (type) {
    "release" -> "正式版"
    "snapshot" -> "快照版"
    "old_beta" -> "远古版 Beta"
    "old_alpha" -> "远古版 Alpha"
    else -> type
}
// rebuild
