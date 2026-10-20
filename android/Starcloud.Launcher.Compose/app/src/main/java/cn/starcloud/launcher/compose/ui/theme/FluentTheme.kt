// Fluent 主题：复刻 Win 端（WinUI 3）配色与形态语言。
// 深色：窗体 #161616、卡片层 rgba(255,255,255,0.06)、品牌黄 #FFD322 强调（对齐桌面 App.xaml）；
// 浅色：窗体 #F6F6F6、卡片白。圆角 4/8/16 三档对齐 WinUI ControlCornerRadius 体系。
package cn.starcloud.launcher.compose.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

object FluentDimens {
    val PagePadding = 16.dp
    val CardPadding = 14.dp
    val CornerRadiusSmall = 4.dp
    val CornerRadius = 8.dp
    val CornerRadiusCard = 12.dp
    val CornerRadiusLarge = 16.dp
    val TopBarHeight = 44.dp
    val NavRailWidth = 64.dp
}

/** Win 端品牌色 */
object FluentColors {
    val Accent = Color(0xFFFFD322)          // 品牌黄（桌面 SystemAccentColor）
    val OnAccent = Color(0xFF1A1A1A)        // accent 面上的黑字
    val Success = Color(0xFF3DDC84)         // 品牌绿（就绪/成功）
    val Danger = Color(0xFFF44336)          // 危险红
    val Warning = Color(0xFFF59E0B)         // 警示琥珀

    // 深色主题（桌面 Dark：窗体 #161616，卡片层半透明白）
    val DarkWindow = Color(0xFF161616)
    val DarkCard = Color(0xFF2A2A2A)
    val DarkCardBorder = Color(0x1FFFFFFF)
    val DarkTextPrimary = Color(0xFFFFFFFF)
    val DarkTextSecondary = Color(0xB3FFFFFF)
    val DarkTextTertiary = Color(0x80FFFFFF)
    val DarkDivider = Color(0x1FFFFFFF)
    val DarkControl = Color(0xFF2C2C2C)
    val DarkControlHover = Color(0xFF353535)

    // 浅色主题（桌面 Light：窗体 #F6F6F6，卡片白）
    val LightWindow = Color(0xFFF6F6F6)
    val LightCard = Color(0xFFFFFFFF)
    val LightCardBorder = Color(0x1F000000)
    val LightTextPrimary = Color(0xFF1A1A1A)
    val LightTextSecondary = Color(0x991A1A1A)
    val LightTextTertiary = Color(0x661A1A1A)
    val LightDivider = Color(0x1F000000)
    val LightControl = Color(0xFFECECEC)
    val LightControlHover = Color(0xFFE0E0E0)
}

private fun fluentDark() = darkColorScheme(
    primary = FluentColors.Accent,
    onPrimary = FluentColors.OnAccent,
    primaryContainer = FluentColors.Accent,
    onPrimaryContainer = FluentColors.OnAccent,
    secondary = FluentColors.Accent,
    background = FluentColors.DarkWindow,
    onBackground = FluentColors.DarkTextPrimary,
    surface = FluentColors.DarkWindow,
    onSurface = FluentColors.DarkTextPrimary,
    surfaceVariant = FluentColors.DarkCard,
    onSurfaceVariant = FluentColors.DarkTextSecondary,
    surfaceContainer = FluentColors.DarkCard,
    surfaceContainerHigh = FluentColors.DarkControl,
    surfaceContainerHighest = FluentColors.DarkControlHover,
    outline = FluentColors.DarkCardBorder,
    outlineVariant = FluentColors.DarkDivider,
    error = FluentColors.Danger,
)

private fun fluentLight() = lightColorScheme(
    primary = FluentColors.Accent,
    onPrimary = FluentColors.OnAccent,
    primaryContainer = FluentColors.Accent,
    onPrimaryContainer = FluentColors.OnAccent,
    secondary = FluentColors.Accent,
    background = FluentColors.LightWindow,
    onBackground = FluentColors.LightTextPrimary,
    surface = FluentColors.LightWindow,
    onSurface = FluentColors.LightTextPrimary,
    surfaceVariant = FluentColors.LightCard,
    onSurfaceVariant = FluentColors.LightTextSecondary,
    surfaceContainer = FluentColors.LightCard,
    surfaceContainerHigh = FluentColors.LightControl,
    surfaceContainerHighest = FluentColors.LightControlHover,
    outline = FluentColors.LightCardBorder,
    outlineVariant = FluentColors.LightDivider,
    error = FluentColors.Danger,
)

/** 跟随系统深浅色（对齐桌面端 AppearanceService 跟随系统） */
@Composable
fun FluentTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) fluentDark() else fluentLight(),
        content = content,
    )
}
