// 账户页（M4 离线先行）：离线账户列表 + 添加 / 设为启动身份 / 删除。
// 正版（微软设备码）与第三方（authlib）账户在后续里程碑接入同一列表。
package cn.starcloud.launcher.compose.screens

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import cn.starcloud.launcher.compose.data.AccountsStore
import cn.starcloud.launcher.compose.data.OfflineAccount
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens

@Composable
fun AccountsScreen(context: Context, refreshKey: Int, onChanged: () -> Unit) {
    var accounts by remember(refreshKey) { mutableStateOf(AccountsStore.list(context)) }
    var selectedName by remember(refreshKey) {
        mutableStateOf(AccountsStore.selected(context)?.name ?: "")
    }
    var showAdd by remember { mutableStateOf(false) }
    var pendingDelete by remember { mutableStateOf<OfflineAccount?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    fun reload() {
        accounts = AccountsStore.list(context)
        selectedName = AccountsStore.selected(context)?.name ?: ""
        onChanged()
    }

    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        PageHeader(title = "账户", actionLabel = "添加账户", onAction = { showAdd = true })

        if (accounts.isEmpty()) {
            Column(
                Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text("还没有账户", color = MaterialTheme.colorScheme.primary)
                Text(
                    "点右上「添加账户」创建一个离线账户",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                )
            }
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                items(accounts, key = { it.name }) { account ->
                    val isSelected = account.name == selectedName
                    Card(
                        onClick = {
                            AccountsStore.setSelected(context, account.name)
                            selectedName = account.name
                            onChanged()
                        },
                        modifier = Modifier.fillMaxWidth(),
                        colors = CardDefaults.cardColors(
                            containerColor = if (isSelected) MaterialTheme.colorScheme.surfaceContainerHighest
                            else MaterialTheme.colorScheme.surfaceContainer,
                        ),
                        shape = RoundedCornerShape(FluentDimens.CornerRadius),
                    ) {
                        Row(
                            Modifier.fillMaxWidth().padding(12.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text(account.name, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                                    if (isSelected) {
                                        androidx.compose.foundation.layout.Spacer(Modifier.padding(start = 6.dp))
                                        Text(
                                            "启动身份",
                                            fontSize = 10.sp,
                                            color = FluentColors.OnAccent,
                                            modifier = Modifier
                                                .background(
                                                    FluentColors.Accent,
                                                    RoundedCornerShape(FluentDimens.CornerRadiusSmall),
                                                )
                                                .padding(horizontal = 6.dp, vertical = 2.dp),
                                        )
                                    }
                                }
                                Text(
                                    "离线账户 · ${account.uuid.take(13)}…",
                                    fontSize = 11.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                )
                            }
                            TextButton(onClick = { pendingDelete = account }) { Text("删除") }
                        }
                    }
                }
            }
        }
    }

    if (showAdd) {
        var name by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { showAdd = false },
            title = { Text("添加离线账户") },
            text = {
                Column {
                    OutlinedTextField(
                        value = name,
                        onValueChange = { name = it },
                        placeholder = { Text("玩家名（3-16 位字母/数字/下划线）") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    error?.let {
                        androidx.compose.foundation.layout.Spacer(Modifier.padding(top = 6.dp))
                        Text(it, fontSize = 12.sp, color = FluentColors.Danger)
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        if (AccountsStore.add(context, name)) {
                            showAdd = false
                            error = null
                            reload()
                        } else {
                            error = "名字不合法（3-16 位字母/数字/下划线），或已存在"
                        }
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = FluentColors.Accent,
                        contentColor = FluentColors.OnAccent,
                    ),
                ) { Text("添加") }
            },
            dismissButton = {
                TextButton(onClick = { showAdd = false }) { Text("取消") }
            },
        )
    }

    pendingDelete?.let { account ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("删除账户") },
            text = { Text("确定删除离线账户「${account.name}」吗？") },
            confirmButton = {
                TextButton(onClick = {
                    AccountsStore.remove(context, account.name)
                    pendingDelete = null
                    reload()
                }) { Text("删除", color = FluentColors.Danger) }
            },
            dismissButton = {
                TextButton(onClick = { pendingDelete = null }) { Text("取消") }
            },
        )
    }
}
