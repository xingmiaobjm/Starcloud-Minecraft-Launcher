// Fluent 页面共享件：内容页页头（大标题 + 右侧动作），对齐 Win 端 NavigationView 内容页视觉。
// 注：动作参数必须显式传入（无默认值、无分支包装、不读 MaterialTheme 色）——本工具链
// （Kotlin 2.0.20 + Compose BOM 2024.09）下带默认参数的版本实测标题整体不可见
// （字形未绘制），六轮真机对照定位；详见会话记录，升级工具链后可复验放宽。
package cn.starcloud.launcher.compose.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

@Composable
fun PageHeader(title: String, actionLabel: String, onAction: () -> Unit) {
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(title, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
        TextButton(onClick = onAction) {
            Text(actionLabel)
        }
    }
}
