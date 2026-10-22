// Starcloud ID 登录弹窗（设置页）：StarcloudID（用户名）+ 密码，与电脑端一致。
// 登录两步换 Key（login → apikeys），api_key 持久化本机、不回显。
package cn.starcloud.launcher.compose.screens

import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.AuthApi
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun StarcloudLoginDialog(
    context: Context,
    onDismiss: () -> Unit,
    onLoggedIn: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var id by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text("登录 Starcloud ID") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = id,
                    onValueChange = { id = it },
                    label = { Text("StarcloudID（用户名）") },
                    singleLine = true,
                    enabled = !busy,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text("密码") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    enabled = !busy,
                    modifier = Modifier.fillMaxWidth(),
                )
                Text(
                    "与电脑端同一账号；凭证保存在本机，不会回显。",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                )
                error?.let {
                    Text(it, fontSize = 12.sp, color = FluentColors.Danger)
                }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (id.isBlank() || password.isBlank()) {
                        error = "请填写 StarcloudID 与密码"
                        return@Button
                    }
                    busy = true
                    error = null
                    scope.launch {
                        try {
                            val session = withContext(Dispatchers.IO) {
                                AuthApi.login(id, password)
                            }
                            LauncherPrefs.setStarcloudUid(context, session.userId)
                            LauncherPrefs.setStarcloudKey(context, session.apiKey)
                            busy = false
                            android.widget.Toast
                                .makeText(context, "登录成功，欢迎 ${session.displayName}", android.widget.Toast.LENGTH_SHORT)
                                .show()
                            onLoggedIn()
                            onDismiss()
                        } catch (e: Exception) {
                            busy = false
                            error = e.message ?: e.javaClass.simpleName
                            android.widget.Toast
                                .makeText(context, "登录失败：${error}", android.widget.Toast.LENGTH_LONG)
                                .show()
                        }
                    }
                },
                enabled = !busy,
                colors = ButtonDefaults.buttonColors(
                    containerColor = FluentColors.Accent,
                    contentColor = FluentColors.OnAccent,
                ),
            ) { Text(if (busy) "登录中…" else "登录") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, enabled = !busy) { Text("取消") }
        },
    )
}
