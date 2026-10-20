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

        // 游戏链组件（jre/lwjgl/mesa）只有 arm64-v8a 构建
        ndk {
            abiFilters += "arm64-v8a"
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
