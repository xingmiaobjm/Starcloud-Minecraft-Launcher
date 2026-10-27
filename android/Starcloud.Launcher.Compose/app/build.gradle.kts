plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "cn.starcloud.launcher.compose"
    compileSdk = 36

    defaultConfig {
        applicationId = "cn.starcloud.launcher.compose"
        minSdk = 26
        targetSdk = 28 // SELinux 对 targetSdk>=29 禁 app data 目录 execve（JLI/Launcher 需 exec bin/java）
        versionCode = 1
        versionName = "0.1.0"

        // 游戏链组件（jre/lwjgl/mesa）只有 arm64-v8a 构建；
        // 但 Web 壳与桥的 UI 层可以在 x86_64 模拟器上验证，用 -PemuAbi 切过去：
        //   gradle :app:assembleDebug -PemuAbi
        // （x86_64 包装得上但 launch.start 会在 JVM 阶段失败——native 只有 arm64）
        ndk {
            abiFilters += if (project.hasProperty("emuAbi")) listOf("x86_64") else listOf("arm64-v8a")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    // 宿主 Java 层（SDL/FCL 桥）为 UTF-8 中文注释源，Windows 平台默认 GBK 不可映射
    tasks.withType<JavaCompile>().configureEach {
        options.encoding = "UTF-8"
    }
    buildFeatures {
        compose = true
    }
    sourceSets {
        getByName("main") {
            java.srcDirs("src/main/java")
            kotlin.srcDirs("src/main/java")
        }
    }
    // libstarcloudjvm.so：JNI_CreateJavaVM 封装（dlopen 链 + JVM 拉起 + main 调用）
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }
}

/**
 * 把 Electron 的 Web 前端产物同步到安卓 Web 壳的 assets。
 *
 * 两端跑的是同一份 React 页面，但产物是两份（electron/out/renderer 与
 * android/.../assets/web/app）。以前靠手工拷贝，产物与源码会漂移 ——
 * 安卓上看到的还是旧界面还查不出来。现在有 Gradle 任务兜着：
 *   gradle :app:syncWebAssets
 * 需要先在 electron/ 下 npm run build。
 */
val syncWebAssets by tasks.registering(Copy::class) {
    group = "web"
    description = "同步 Electron 的 Web 前端产物到安卓 Web 壳 assets"
    val rendererDir = rootProject.file("../../electron/out/renderer")
    val webAppDir = file("src/main/assets/web/app")
    doFirst {
        require(rendererDir.exists()) {
            "找不到 Electron 构建产物 ${rendererDir}：先在 electron/ 下执行 npm run build"
        }
    }
    from(rendererDir) {
        into(webAppDir)
    }
    // index.html 里引用的是带内容哈希的文件名（index-<hash>.js），复制后要改写
    doLast {
        val assetsDir = File(webAppDir, "assets")
        val indexHtml = File(webAppDir, "index.html")
        if (indexHtml.exists() && assetsDir.exists()) {
            val js = assetsDir.listFiles()?.firstOrNull { it.name.startsWith("index-") && it.name.endsWith(".js") }?.name
            val css = assetsDir.listFiles()?.firstOrNull { it.name.startsWith("index-") && it.name.endsWith(".css") }?.name
            var html = indexHtml.readText()
            if (js != null) {
                html = html.replace(Regex("src=\"\\./assets/index-[^\"]+\\.js\""), "src=\"./assets/$js\"")
            }
            if (css != null) {
                html = html.replace(Regex("href=\"\\./assets/index-[^\"]+\\.css\""), "href=\"./assets/$css\"")
            }
            indexHtml.writeText(html)
            logger.lifecycle("[syncWebAssets] index.html -> $js / $css")
        }
    }
}

// 打包前自动同步：安卓 APK 里的 Web 界面永远跟着 electron 源码走
tasks.named("preBuild") {
    dependsOn("syncWebAssets")
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.03")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
    // 组件部署：jre 组件为 tar.xz（zip 走 java.util.zip 内置）
    implementation("org.apache.commons:commons-compress:1.26.2")
    implementation("org.tukaani:xz:1.9")
}
