// JVM 拉起 native 封装：libstarcloudjvm.so（app/src/main/cpp/starcloud_jvm.c）。
// 全链：dlopen 组件 so（RTLD_GLOBAL）→ stdout/stderr 重定向 → chdir/setenv →
// JNI_CreateJavaVM → main(String[]) → DestroyJavaVM。
package cn.starcloud.launcher.compose.game

object StarcloudJvm {
    init {
        System.loadLibrary("starcloudjvm")
    }

    /** 环境预置（SDL 活动启动前必须完成——钩子安装时对环境变量做快照） */
    external fun starcloudSetEnv(name: String, value: String): Int

    /** JLI_Launch 路径（bin/java 同代码路径）：绕开 MobiPort JNI_CreateJavaVM 的类加载期 SIGSEGV。
        游戏退出时 JLI 内部 exit 整个进程 */
    external fun starcloudRunJvmJli(
        nativeDirs: Array<String>,
        logPath: String,
        cwd: String,
        envPairs: Array<String>,
        options: Array<String>,
        mainClass: String,
        programArgs: Array<String>,
        jliPath: String,
    ): Int

    /** JVM 全生命周期；返回退出码，≥90 为宿主侧错误码（见 C 文件） */
    external fun starcloudRunJvm(
        nativeDirs: Array<String>,
        logPath: String,
        cwd: String,
        envPairs: Array<String>,
        options: Array<String>,
        mainClass: String,
        programArgs: Array<String>,
    ): Int
}
