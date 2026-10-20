// 组件部署：assets/components 内置包 → 校验 sha256 → 解压 → 写 .starcloud-ok 标记。
// 目录约定 components/<名称>/<版本>/（对齐 C# DeploymentPlanner），标记存在即视为就位。
package cn.starcloud.launcher.compose.data

import android.content.Context
import org.apache.commons.compress.archivers.tar.TarArchiveEntry
import org.apache.commons.compress.archivers.tar.TarArchiveInputStream
import org.apache.commons.compress.archivers.zip.ZipArchiveInputStream
import org.apache.commons.compress.compressors.xz.XZCompressorInputStream
import java.io.File
import java.io.FileInputStream
import java.security.MessageDigest
import java.util.zip.ZipEntry

object ComponentDeployer {

    const val OK_MARKER = ".starcloud-ok"

    fun componentDir(componentsRoot: File, name: String, version: String) =
        File(componentsRoot, "$name/$version")

    fun isDeployed(componentsRoot: File, entry: ComponentEntry): Boolean {
        val marker = File(componentDir(componentsRoot, entry.name, entry.version), OK_MARKER)
        return marker.isFile
    }

    /**
     * 确保全部内置组件就位；返回解析后的组件清单。
     * onProgress(phase, current, total) 供 UI 显示部署进度（首启解压 349M 需数分钟）。
     */
    /**
     * 外部导入：getExternalFilesDir 下的 import-<名称>.tar.gz 会被解压到 components/<名称>/
     * 并写 .starcloud-ok（内容为完整组件目录树）。用于 adb push 离线导入组件
     * （run-as 域读外部 FUSE 被拒，须由 app 自身解压）。
     */
    private fun importExternalPackages(context: Context, componentsRoot: File) {
        val external = context.getExternalFilesDir(null) ?: return
        external.listFiles { f -> f.isFile && f.name.startsWith("import-") && f.name.endsWith(".tar.gz") }
            ?.sorted()
            ?.forEach { pkg ->
                val name = pkg.name.removePrefix("import-").removeSuffix(".tar.gz")
                val deployDir = File(componentsRoot, name)
                try {
                    if (deployDir.exists()) deployDir.deleteRecursively()
                    deployDir.mkdirs()
                    java.util.zip.GZIPInputStream(pkg.inputStream()).use { gin ->
                        TarArchiveInputStream(gin).use { tar ->
                            while (true) {
                                val entry = tar.nextEntry ?: break
                                if (entry.isDirectory) continue
                                val out = safeTarget(deployDir, entry.name)
                                out.parentFile?.mkdirs()
                                out.outputStream().use { tar.copyTo(it, 1 shl 16) }
                            }
                        }
                    }
                    File(deployDir, OK_MARKER).writeText("imported")
                    pkg.delete()
                } catch (e: Exception) {
                    // 导入失败保留包与日志，由常规部署/重试接管
                }
            }
    }

    fun ensureComponents(
        context: Context,
        componentsRoot: File,
        cacheRoot: File,
        onProgress: (String, Int, Int) -> Unit = { _, _, _ -> },
    ): ComponentManifest {
        // 外部导入先于 assets 部署（导入产物若覆盖清单组件目录，assets 部署按 marker 跳过）
        importExternalPackages(context, componentsRoot)
        val manifest = ComponentManifest.parse(
            context.assets.open("components/manifest.json").bufferedReader().use { it.readText() }
        )
        val pending = manifest.components.filter { !isDeployed(componentsRoot, it) }
        pending.forEachIndexed { i, entry ->
            onProgress("部署 ${entry.name}", i + 1, pending.size)
            deploy(context, componentsRoot, cacheRoot, entry)
        }
        return manifest
    }

    private fun deploy(context: Context, componentsRoot: File, cacheRoot: File, entry: ComponentEntry) {
        val deployDir = componentDir(componentsRoot, entry.name, entry.version)
        if (File(deployDir, OK_MARKER).isFile) return

        // ① 缓存：assets → cache/components/<名称>-<版本><扩展名>（断点复用，跳过重复解压）
        val cacheFile = File(File(cacheRoot, "components"), entry.cacheName)
        if (!cacheFile.isFile) {
            cacheFile.parentFile?.mkdirs()
            context.assets.open("components/${entry.assetFile}").use { input ->
                cacheFile.outputStream().use { input.copyTo(it, 1 shl 16) }
            }
        }

        // ② 校验 sha256
        if (entry.sha256.isNotEmpty()) {
            val digest = MessageDigest.getInstance("SHA-256")
                .digest(FileInputStream(cacheFile).use { it.readBytes() })
                .joinToString("") { "%02x".format(it) }
            if (!digest.equals(entry.sha256, ignoreCase = true)) {
                cacheFile.delete()
                throw IllegalStateException("组件 ${entry.name} 包校验失败（sha256 不符）")
            }
        }

        // ③ 解压（先清目标目录避免残留文件混入新版本）
        if (deployDir.exists()) deployDir.deleteRecursively()
        deployDir.mkdirs()
        when (entry.archive) {
            ArchiveKind.TarXz -> extractTarXz(cacheFile, deployDir)
            ArchiveKind.Zip -> extractZip(cacheFile, deployDir)
        }

        // ④ 完成标记
        File(deployDir, OK_MARKER).writeText(entry.version)
    }

    private val ComponentEntry.cacheName: String
        get() = "$name-$version" + when (archive) {
            ArchiveKind.TarXz -> ".tar.xz"
            ArchiveKind.Zip -> ".zip"
        }

    /** 解压 tar.xz；防 zip-slip：条目规范路径必须落在目标目录内 */
    private fun extractTarXz(archive: File, target: File) {
        FileInputStream(archive).use { fin ->
            TarArchiveInputStream(XZCompressorInputStream(fin)).use { tar ->
                while (true) {
                    val entry: TarArchiveEntry = tar.nextEntry ?: break
                    if (!entry.isFile) continue
                    val out = safeTarget(target, entry.name)
                    out.parentFile?.mkdirs()
                    out.outputStream().use { tar.copyTo(it, 1 shl 16) }
                }
            }
        }
    }

    private fun extractZip(archive: File, target: File) {
        ZipArchiveInputStream(FileInputStream(archive)).use { zip ->
            while (true) {
                val entry: ZipEntry = zip.nextEntry ?: break
                if (entry.isDirectory) continue
                val out = safeTarget(target, entry.name)
                out.parentFile?.mkdirs()
                out.outputStream().use { zip.copyTo(it, 1 shl 16) }
            }
        }
    }

    private fun safeTarget(target: File, entryName: String): File {
        val out = File(target, entryName)
        if (!out.canonicalPath.startsWith(target.canonicalPath + File.separator)) {
            throw IllegalStateException("组件包内路径越界：$entryName")
        }
        return out
    }
}
