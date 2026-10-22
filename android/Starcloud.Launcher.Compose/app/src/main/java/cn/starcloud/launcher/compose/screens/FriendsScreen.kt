// 好友页（QQ 两栏式，联机并入第二个标签，对齐 Win 端 FriendsPage）：
//   左栏 = 我的资料 + 标签（消息 / 好友申请 / 联机）+ 对应列表（未读角标 / 接受·拒绝 / 邀请卡片）
//   右栏 = 选中好友的聊天（气泡 + 输入框）/ 申请详情 / 联机（发起邀请表单 + 已接受邀请地址）
//   登录态来自 LauncherPrefs（Starcloud ID）；SSE 长连接实时刷新，断线指数退避重连
package cn.starcloud.launcher.compose.screens

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
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import cn.starcloud.launcher.compose.data.FriendsApi
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.data.MultiplayerController
import cn.starcloud.launcher.compose.game.FloatingOverlayService
import cn.starcloud.launcher.compose.ui.theme.FluentColors
import cn.starcloud.launcher.compose.ui.theme.FluentDimens
import androidx.compose.runtime.collectAsState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter

@Composable
fun FriendsScreen(refreshKey: Int, initialTab: Int = 0) {
    val ctx = LocalContext.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    val apiKey = remember { LauncherPrefs.starcloudKey(ctx) }
    val myUid = remember { LauncherPrefs.starcloudUid(ctx) }

    var friends by remember { mutableStateOf<List<FriendsApi.Friend>>(emptyList()) }
    var requests by remember { mutableStateOf(FriendsApi.Requests(emptyList(), emptyList())) }
    var invites by remember { mutableStateOf(FriendsApi.Invites(emptyList(), emptyList())) }
    var tab by remember { mutableIntStateOf(initialTab) } // 0=消息 1=好友申请 2=联机
    var selectedPeer by remember { mutableStateOf<FriendsApi.Friend?>(null) }
    val messages = remember { mutableStateListOf<FriendsApi.Message>() }
    var input by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var notice by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableIntStateOf(0) } // SSE/手动触发重拉
    var showAddDialog by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()

    fun refreshAll() {
        scope.launch {
            try {
                val (f, r, i) = withContext(Dispatchers.IO) {
                    Triple(FriendsApi.friends(apiKey), FriendsApi.requests(apiKey), FriendsApi.invites(apiKey))
                }
                friends = f
                requests = r
                invites = i
                error = null
            } catch (e: Exception) {
                error = e.message ?: e.javaClass.simpleName
            }
        }
    }

    LaunchedEffect(refreshKey, tick) {
        if (apiKey.isNotBlank()) refreshAll()
    }

    // SSE 实时事件 → 触发重拉（指数退避重连）
    LaunchedEffect(apiKey) {
        if (apiKey.isBlank()) return@LaunchedEffect
        var backoff = 2_000L
        while (isActive) {
            try {
                withContext(Dispatchers.IO) { FriendsApi.streamEvents(apiKey) { _, _ -> } }
                tick++
                backoff = 2_000L
            } catch (e: Exception) {
                delay(backoff)
                backoff = (backoff * 2).coerceAtMost(60_000L)
            }
        }
    }

    // 选中好友 → 拉会话 + 标已读
    LaunchedEffect(selectedPeer?.profile?.userId, tick) {
        val peer = selectedPeer ?: return@LaunchedEffect
        try {
            val conv = withContext(Dispatchers.IO) {
                FriendsApi.conversation(apiKey, peer.profile.userId)
            }
            messages.clear()
            messages.addAll(conv.items)
            withContext(Dispatchers.IO) { FriendsApi.markRead(apiKey, peer.profile.userId) }
            listState.scrollToItem((messages.size - 1).coerceAtLeast(0))
        } catch (e: Exception) {
            // 会话拉取失败不打断页面
        }
    }

    if (apiKey.isBlank()) {
        Box(Modifier.fillMaxSize().padding(16.dp), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text("尚未登录 Starcloud ID", fontSize = 15.sp, fontWeight = FontWeight.Medium)
                Spacer(Modifier.height(6.dp))
                Text(
                    "先到「设置」登录，即可使用好友与联机。",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                )
            }
        }
        return
    }

    fun run(block: () -> Unit, ok: (() -> Unit)? = null) {
        busy = true
        error = null
        scope.launch {
            try {
                withContext(Dispatchers.IO) { block() }
                busy = false
                ok?.invoke()
            } catch (e: Exception) {
                busy = false
                error = e.message ?: e.javaClass.simpleName
            }
        }
    }

    Row(Modifier.fillMaxSize().padding(10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {

        // ── 左栏：我的资料 + 标签 + 列表 ──
        Card(
            Modifier.width(240.dp).fillMaxHeight(),
            colors = CardDefaults.cardColors(),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
        ) {
            Column(Modifier.fillMaxWidth().padding(8.dp)) {
                // 我的资料
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Avatar(myUid.take(1).uppercase(), 26.dp)
                    Spacer(Modifier.width(8.dp))
                    Column(Modifier.weight(1f)) {
                        Text("我", fontSize = 13.sp, fontWeight = FontWeight.Medium)
                        Text(
                            myUid,
                            fontSize = 10.sp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                        )
                    }
                    Text(
                        "＋",
                        fontSize = 16.sp,
                        color = FluentColors.Accent,
                        modifier = Modifier
                            .clickable(enabled = !busy) { showAddDialog = true }
                            .padding(4.dp),
                    )
                }
                Spacer(Modifier.height(6.dp))

                // 标签
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    listOf("消息", "好友申请", "联机").forEachIndexed { index, label ->
                        val selected = tab == index
                        val badge = when (index) {
                            1 -> requests.incoming.size
                            else -> 0
                        }
                        Row(
                            Modifier
                                .background(
                                    if (selected) FluentColors.Accent
                                    else MaterialTheme.colorScheme.surfaceContainer,
                                    RoundedCornerShape(FluentDimens.CornerRadius),
                                )
                                .clickable { tab = index; selectedPeer = null }
                                .padding(horizontal = 8.dp, vertical = 3.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(
                                label,
                                fontSize = 11.sp,
                                color = if (selected) FluentColors.OnAccent
                                else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                            )
                            if (badge > 0) {
                                Spacer(Modifier.width(4.dp))
                                Text(
                                    "$badge",
                                    fontSize = 9.sp,
                                    color = FluentColors.OnAccent,
                                    modifier = Modifier
                                        .background(Color(0xFFD13438), CircleShape)
                                        .padding(horizontal = 4.dp, vertical = 1.dp),
                                )
                            }
                        }
                    }
                }
                Spacer(Modifier.height(6.dp))

                // 列表
                when (tab) {
                    0 -> LazyColumn(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        items(friends, key = { it.profile.userId }) { f ->
                            val selected = selectedPeer?.profile?.userId == f.profile.userId
                            Row(
                                Modifier
                                    .fillMaxWidth()
                                    .background(
                                        if (selected) MaterialTheme.colorScheme.surfaceContainerHighest
                                        else Color.Transparent,
                                        RoundedCornerShape(FluentDimens.CornerRadius),
                                    )
                                    .clickable {
                                        selectedPeer = f
                                        tab = 0
                                    }
                                    .padding(horizontal = 6.dp, vertical = 6.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Avatar(f.profile.displayName.take(1), 22.dp)
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    f.profile.displayName,
                                    fontSize = 12.sp,
                                    modifier = Modifier.weight(1f),
                                    maxLines = 1,
                                )
                                if (f.unread > 0) {
                                    Text(
                                        "${f.unread}",
                                        fontSize = 9.sp,
                                        color = FluentColors.OnAccent,
                                        modifier = Modifier
                                            .background(FluentColors.Accent, CircleShape)
                                            .padding(horizontal = 5.dp, vertical = 1.dp),
                                    )
                                }
                            }
                        }
                        if (friends.isEmpty()) {
                            item {
                                Text(
                                    "还没有好友，点右上角 ＋ 添加。",
                                    fontSize = 11.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                    modifier = Modifier.padding(8.dp),
                                )
                            }
                        }
                    }
                    1 -> LazyColumn(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        item {
                            Text(
                                "收到的申请",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                                modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp),
                            )
                        }
                        items(requests.incoming, key = { it.requestId }) { r ->
                            RequestCard(
                                r,
                                onAccept = {
                                    run({ FriendsApi.acceptRequest(apiKey, r.requestId) }) {
                                        notice = "已添加 ${r.user.displayName}"
                                        tick++
                                    }
                                },
                                onReject = {
                                    run({ FriendsApi.rejectRequest(apiKey, r.requestId) }) { tick++ }
                                },
                            )
                        }
                        item {
                            Text(
                                "发出的申请",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                                modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp),
                            )
                        }
                        items(requests.outgoing, key = { it.requestId }) { r ->
                            Row(
                                Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 2.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Avatar(r.user.displayName.take(1), 18.dp)
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    r.user.displayName,
                                    fontSize = 11.sp,
                                    modifier = Modifier.weight(1f),
                                    maxLines = 1,
                                )
                                Text(
                                    "撤回",
                                    fontSize = 10.sp,
                                    color = FluentColors.Accent,
                                    modifier = Modifier
                                        .clickable(enabled = !busy) {
                                            run({ FriendsApi.withdrawRequest(apiKey, r.requestId) }) { tick++ }
                                        }
                                        .padding(4.dp),
                                )
                            }
                        }
                        if (requests.incoming.isEmpty() && requests.outgoing.isEmpty()) {
                            item {
                                Text(
                                    "暂无好友申请。",
                                    fontSize = 11.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                    modifier = Modifier.padding(8.dp),
                                )
                            }
                        }
                    }
                    2 -> LazyColumn(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        item {
                            Text(
                                "收到的联机邀请",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                                modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp),
                            )
                        }
                        items(invites.incoming, key = { it.inviteId }) { inv ->
                            InviteCard(
                                inv,
                                onAccept = {
                                    run({ FriendsApi.acceptInvite(apiKey, inv.inviteId) }) { tick++ }
                                },
                                onDecline = {
                                    run({ FriendsApi.declineInvite(apiKey, inv.inviteId) }) { tick++ }
                                },
                            )
                        }
                        item {
                            Text(
                                "发出的联机邀请",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                                modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp),
                            )
                        }
                        items(invites.outgoing, key = { it.inviteId }) { inv ->
                            Row(
                                Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 2.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Avatar(inv.user.displayName.take(1), 18.dp)
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    "${inv.user.displayName} · ${inv.connectionText}",
                                    fontSize = 11.sp,
                                    modifier = Modifier.weight(1f),
                                    maxLines = 1,
                                )
                                Text(
                                    "撤回",
                                    fontSize = 10.sp,
                                    color = FluentColors.Accent,
                                    modifier = Modifier
                                        .clickable(enabled = !busy) {
                                            run({ FriendsApi.cancelInvite(apiKey, inv.inviteId) }) { tick++ }
                                        }
                                        .padding(4.dp),
                                )
                            }
                        }
                        if (invites.incoming.isEmpty() && invites.outgoing.isEmpty()) {
                            item {
                                Text(
                                    "暂无联机邀请。在右栏向好友发起。",
                                    fontSize = 11.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                    modifier = Modifier.padding(8.dp),
                                )
                            }
                        }
                    }
                }
            }
        }

        // ── 右栏：聊天 / 详情 / 联机发起 ──
        Card(
            Modifier.weight(1f).fillMaxHeight(),
            colors = CardDefaults.cardColors(),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
        ) {
            when (tab) {
                0 -> {
                    val peer = selectedPeer
                    if (peer == null) {
                        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Text(
                                "选择一个好友开始聊天",
                                fontSize = 12.sp,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                            )
                        }
                    } else {
                        Column(Modifier.fillMaxSize().padding(10.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Avatar(peer.profile.displayName.take(1), 20.dp)
                                Spacer(Modifier.width(8.dp))
                                Text(peer.profile.displayName, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    peer.profile.id,
                                    fontSize = 10.sp,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                )
                                Spacer(Modifier.weight(1f))
                                Text(
                                    "删除",
                                    fontSize = 11.sp,
                                    color = Color(0xFFD13438),
                                    modifier = Modifier
                                        .clickable(enabled = !busy) {
                                            run({ FriendsApi.removeFriend(apiKey, peer.profile.userId) }) {
                                                selectedPeer = null
                                                tick++
                                            }
                                        }
                                        .padding(4.dp),
                                )
                            }
                            Spacer(Modifier.height(6.dp))

                            LazyColumn(
                                Modifier.weight(1f).fillMaxWidth(),
                                state = listState,
                                verticalArrangement = Arrangement.spacedBy(6.dp),
                            ) {
                                items(messages, key = { it.id }) { m ->
                                    val mine = m.fromUserId == myUid
                                    Row(
                                        Modifier.fillMaxWidth(),
                                        horizontalArrangement = if (mine) Arrangement.End else Arrangement.Start,
                                    ) {
                                        if (!mine) {
                                            Avatar(peer.profile.displayName.take(1), 18.dp)
                                            Spacer(Modifier.width(6.dp))
                                        }
                                        Column(horizontalAlignment = if (mine) Alignment.End else Alignment.Start) {
                                            Text(
                                                m.content,
                                                fontSize = 13.sp,
                                                color = if (mine) FluentColors.OnAccent
                                                else MaterialTheme.colorScheme.onSurface,
                                                modifier = Modifier
                                                    .background(
                                                        if (mine) FluentColors.Accent
                                                        else MaterialTheme.colorScheme.surfaceContainerHigh,
                                                        RoundedCornerShape(10.dp),
                                                    )
                                                    .padding(horizontal = 10.dp, vertical = 6.dp),
                                            )
                                            Text(
                                                fmtTime(m.createdAt),
                                                fontSize = 9.sp,
                                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.45f),
                                                modifier = Modifier.padding(top = 1.dp),
                                            )
                                        }
                                    }
                                }
                                if (messages.isEmpty()) {
                                    item {
                                        Text(
                                            "还没有消息，打个招呼吧～",
                                            fontSize = 11.sp,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.45f),
                                            modifier = Modifier.padding(8.dp),
                                        )
                                    }
                                }
                            }

                            error?.let {
                                Text(it, fontSize = 11.sp, color = FluentColors.Danger)
                            }
                            notice?.let {
                                Text(it, fontSize = 11.sp, color = FluentColors.Accent)
                            }
                            Row(
                                Modifier.fillMaxWidth().padding(top = 6.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(6.dp),
                            ) {
                                OutlinedTextField(
                                    value = input,
                                    onValueChange = { input = it },
                                    modifier = Modifier.weight(1f).height(52.dp),
                                    placeholder = { Text("输入消息…", fontSize = 12.sp) },
                                    singleLine = true,
                                    textStyle = androidx.compose.ui.text.TextStyle(fontSize = 13.sp),
                                )
                                Button(
                                    onClick = {
                                        val text = input.trim()
                                        if (text.isEmpty()) return@Button
                                        input = ""
                                        run({ FriendsApi.sendMessage(apiKey, peer.profile.userId, text) }) {
                                            tick++
                                        }
                                    },
                                    enabled = !busy,
                                    colors = ButtonDefaults.buttonColors(
                                        containerColor = FluentColors.Accent,
                                        contentColor = FluentColors.OnAccent,
                                    ),
                                    shape = RoundedCornerShape(FluentDimens.CornerRadius),
                                ) {
                                    Text("发送", fontSize = 12.sp)
                                }
                            }
                        }
                    }
                }
                1 -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text(
                        "在左栏处理好友申请",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                    )
                }
                2 -> MultiplayerPanel(
                    friends = friends,
                    busy = busy,
                    error = error,
                    accepted = invites.incoming.filter { it.status == "accepted" },
                    onSend = { target, address, port, roomCode, message ->
                        run(
                            {
                                val peer = friends.firstOrNull { it.profile.userId == target }
                                    ?: friends.firstOrNull { it.profile.id == target }
                                    ?: throw IllegalStateException("请选择好友")
                                FriendsApi.sendInvite(
                                    apiKey, peer.profile.userId, address, port, roomCode, message,
                                )
                            },
                        ) { tick++ }
                    },
                )
            }
        }
    }

    if (showAddDialog) {
        AddFriendDialog(
            busy = busy,
            error = error,
            onDismiss = { showAddDialog = false; error = null },
            onSend = { target, message ->
                run({ FriendsApi.sendRequest(apiKey, target, message) }) {
                    notice = "申请已发送"
                    showAddDialog = false
                    tick++
                }
            },
            onSearch = { query ->
                FriendsApi.search(apiKey, query)
            },
        )
    }
}

/** 联机面板：联机状态卡（开/关房间）+ 自动联机/悬浮窗开关 + 向好友发起邀请（预填当前房间） */
@Composable
private fun MultiplayerPanel(
    friends: List<FriendsApi.Friend>,
    busy: Boolean,
    error: String?,
    accepted: List<FriendsApi.Invite>,
    onSend: (target: String, address: String?, port: Int?, roomCode: String?, message: String?) -> Unit,
) {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val mp by MultiplayerController.state.collectAsState()
    var target by remember { mutableStateOf("") }
    var address by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("25565") }
    var roomCode by remember { mutableStateOf("") }
    var message by remember { mutableStateOf("") }
    var autoMp by remember { mutableStateOf(LauncherPrefs.multiplayerAuto(ctx)) }
    var overlayOn by remember { mutableStateOf(LauncherPrefs.multiplayerOverlay(ctx)) }
    var corePort by remember { mutableStateOf(LauncherPrefs.multiplayerPort(ctx).toString()) }
    val scope = androidx.compose.runtime.rememberCoroutineScope()

    // 面板期间轮询联机状态；在房中时邀请的房间码自动跟随当前房间
    androidx.compose.runtime.DisposableEffect(Unit) {
        MultiplayerController.startPolling(scope, ctx, owner = "panel")
        onDispose { MultiplayerController.stopPolling("panel") }
    }
    LaunchedEffect(mp.room) {
        if (mp.room != null) roomCode = mp.room!!
    }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        // ── 联机状态卡 ──
        Card(
            Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
            ),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
        ) {
            Column(
                Modifier.fillMaxWidth().padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text("联机状态", fontSize = 14.sp, fontWeight = FontWeight.Medium)
                Text(
                    when (mp.phase) {
                        MultiplayerController.Phase.CoreMissing -> "未检测到联机核心（本机 ${LauncherPrefs.multiplayerPort(ctx)} 端口无响应）"
                        MultiplayerController.Phase.Idle -> "空闲——游戏内「对局域网开放」后点「开启联机」"
                        MultiplayerController.Phase.HostScanning -> "正在扫描局域网世界…"
                        MultiplayerController.Phase.HostStarting -> "房间启动中…"
                        MultiplayerController.Phase.HostReady -> "房间已开 · 邀请码 ${mp.room ?: "-"}"
                        MultiplayerController.Phase.GuestConnecting -> "连接房间中…"
                        MultiplayerController.Phase.GuestEntering -> "进入虚拟局域网…"
                        MultiplayerController.Phase.GuestReady -> "已联机 · ${mp.address ?: ""}（${mp.players} 人）"
                        MultiplayerController.Phase.Failed -> "联机异常${mp.error?.let { "（$it）" } ?: ""}"
                    },
                    fontSize = 12.sp,
                    color = if (mp.inRoom) FluentColors.Accent
                    else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                )
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    OutlinedTextField(
                        value = corePort,
                        onValueChange = { corePort = it.filter { c -> c.isDigit() }.take(5) },
                        label = { Text("核心端口", fontSize = 10.sp) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.width(110.dp),
                    )
                    OutlinedButton(
                        onClick = {
                            LauncherPrefs.setMultiplayerPort(ctx, corePort.toIntOrNull() ?: 0)
                            scope.launch { MultiplayerController.refresh(ctx) }
                        },
                        enabled = !busy,
                    ) { Text("应用端口", fontSize = 11.sp) }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Button(
                        onClick = {
                            scope.launch {
                                if (mp.inRoom) MultiplayerController.idle(ctx)
                                else MultiplayerController.host(ctx, LauncherPrefs.playerName(ctx))
                            }
                        },
                        enabled = !busy,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = FluentColors.Accent,
                            contentColor = FluentColors.OnAccent,
                        ),
                        shape = RoundedCornerShape(FluentDimens.CornerRadius),
                    ) {
                        Text(if (mp.inRoom) "关闭联机" else "开启联机", fontSize = 12.sp)
                    }
                }
                SwitchRow(
                    label = "游戏开局域网后自动开启联机",
                    hint = "游戏启动成功后自动创建房间（邀请码见上方状态）",
                    checked = autoMp,
                    onChecked = {
                        autoMp = it
                        LauncherPrefs.setMultiplayerAuto(ctx, it)
                    },
                )
                SwitchRow(
                    label = "游戏内悬浮窗（联机控制）",
                    hint = if (FloatingOverlayService.canDraw(ctx)) "游戏中显示悬浮球：状态 / 开关房间 / 邀请好友"
                    else "需要「显示在其他应用上层」权限，点开关去授权",
                    checked = overlayOn,
                    onChecked = { on ->
                        if (on && !FloatingOverlayService.canDraw(ctx)) {
                            ctx.startActivity(
                                android.content.Intent(
                                    android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                                    android.net.Uri.parse("package:cn.starcloud.launcher.compose"),
                                ).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK),
                            )
                            return@SwitchRow
                        }
                        overlayOn = on
                        LauncherPrefs.setMultiplayerOverlay(ctx, on)
                        if (on) FloatingOverlayService.start(ctx) else FloatingOverlayService.stop(ctx)
                    },
                )
            }
        }

        // ── 发起邀请 ──
        Text("发起联机邀请", fontSize = 14.sp, fontWeight = FontWeight.Medium)
        Text(
            if (mp.inRoom) "已处于联机房间，将邀请好友加入当前房间（邀请码自动填入）。"
            else "把电脑端联机地址发给好友；好友接受后即可在游戏中直连加入。",
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
        )
        if (friends.isEmpty()) {
            Text(
                "还没有好友，先在「消息」标签添加。",
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
            )
        } else {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("选择好友", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f))
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    friends.take(6).forEach { f ->
                        val selected = target == f.profile.userId
                        Row(
                            Modifier
                                .background(
                                    if (selected) FluentColors.Accent
                                    else MaterialTheme.colorScheme.surfaceContainer,
                                    RoundedCornerShape(FluentDimens.CornerRadius),
                                )
                                .clickable { target = f.profile.userId }
                                .padding(horizontal = 8.dp, vertical = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Avatar(f.profile.displayName.take(1), 14.dp)
                            Spacer(Modifier.width(4.dp))
                            Text(
                                f.profile.displayName,
                                fontSize = 11.sp,
                                color = if (selected) FluentColors.OnAccent
                                else MaterialTheme.colorScheme.onSurface,
                            )
                        }
                    }
                }
            }
            OutlinedTextField(
                value = address,
                onValueChange = { address = it },
                label = { Text("服务器地址（IP / 域名，可选）", fontSize = 11.sp) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = port,
                onValueChange = { port = it.filter { c -> c.isDigit() }.take(5) },
                label = { Text("端口（可选）", fontSize = 11.sp) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = roomCode,
                onValueChange = { roomCode = it },
                label = { Text(if (mp.inRoom) "房间邀请码（当前房间）" else "房间码（陶瓦联机，可选）", fontSize = 11.sp) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = message,
                onValueChange = { message = it },
                label = { Text("留言（可选）", fontSize = 11.sp) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Button(
                onClick = {
                    onSend(
                        target,
                        address.trim().ifBlank { null },
                        port.toIntOrNull(),
                        roomCode.trim().ifBlank { null },
                        message.trim().ifBlank { null },
                    )
                },
                enabled = !busy && target.isNotBlank() &&
                    (address.isNotBlank() || roomCode.isNotBlank()),
                colors = ButtonDefaults.buttonColors(
                    containerColor = FluentColors.Accent,
                    contentColor = FluentColors.OnAccent,
                ),
                shape = RoundedCornerShape(FluentDimens.CornerRadius),
            ) {
                Text("发送邀请", fontSize = 12.sp)
            }
        }

        if (accepted.isNotEmpty()) {
            Spacer(Modifier.height(4.dp))
            Text("已接受的邀请", fontSize = 14.sp, fontWeight = FontWeight.Medium)
            accepted.forEach { inv ->
                Card(
                    Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
                    ),
                    shape = RoundedCornerShape(FluentDimens.CornerRadius),
                ) {
                    Column(Modifier.fillMaxWidth().padding(10.dp)) {
                        Text("${inv.user.displayName} 的房间", fontSize = 12.sp, fontWeight = FontWeight.Medium)
                        Text(
                            inv.connectionText,
                            fontSize = 12.sp,
                            color = FluentColors.Accent,
                        )
                        OutlinedButton(
                            onClick = {
                                // 一键进房：房间码走陶瓦 guesting，否则填地址提示
                                val room = inv.roomCode
                                if (room != null) {
                                    scope.launch {
                                        MultiplayerController.join(ctx, room, LauncherPrefs.playerName(ctx))
                                    }
                                }
                            },
                            enabled = inv.roomCode != null && !busy,
                        ) {
                            Text(if (inv.roomCode != null) "加入房间" else "仅地址，请手动在游戏中加入", fontSize = 11.sp)
                        }
                    }
                }
            }
        }

        error?.let { Text(it, fontSize = 11.sp, color = FluentColors.Danger) }
    }
}

/** 设置行开关（联机面板内） */
@Composable
private fun SwitchRow(label: String, hint: String, checked: Boolean, onChecked: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(label, fontSize = 12.sp, fontWeight = FontWeight.Medium)
            Text(
                hint,
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
            )
        }
        androidx.compose.material3.Switch(checked = checked, onCheckedChange = onChecked)
    }
}

/** 收到的申请卡：接受 / 拒绝 */
@Composable
private fun RequestCard(r: FriendsApi.RequestView, onAccept: () -> Unit, onReject: () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceContainerHigh, RoundedCornerShape(FluentDimens.CornerRadius))
            .padding(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Avatar(r.user.displayName.take(1), 18.dp)
            Spacer(Modifier.width(6.dp))
            Column(Modifier.weight(1f)) {
                Text(r.user.displayName, fontSize = 11.sp, fontWeight = FontWeight.Medium, maxLines = 1)
                Text(r.user.id, fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f))
            }
        }
        r.message?.let {
            Text(
                it,
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.65f),
                modifier = Modifier.padding(top = 2.dp),
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
            Text(
                "接受",
                fontSize = 10.sp,
                color = FluentColors.OnAccent,
                modifier = Modifier
                    .background(FluentColors.Accent, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                    .clickable(onClick = onAccept)
                    .padding(horizontal = 10.dp, vertical = 3.dp),
            )
            Text(
                "拒绝",
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                modifier = Modifier
                    .background(MaterialTheme.colorScheme.surfaceContainerHighest, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                    .clickable(onClick = onReject)
                    .padding(horizontal = 10.dp, vertical = 3.dp),
            )
        }
    }
}

/** 收到的联机邀请卡：接受 / 拒绝 */
@Composable
private fun InviteCard(inv: FriendsApi.Invite, onAccept: () -> Unit, onDecline: () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceContainerHigh, RoundedCornerShape(FluentDimens.CornerRadius))
            .padding(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Avatar(inv.user.displayName.take(1), 18.dp)
            Spacer(Modifier.width(6.dp))
            Text(
                "${inv.user.displayName} 邀请你联机",
                fontSize = 11.sp,
                fontWeight = FontWeight.Medium,
                maxLines = 1,
            )
        }
        Text(
            inv.connectionText,
            fontSize = 11.sp,
            color = FluentColors.Accent,
            modifier = Modifier.padding(top = 2.dp),
        )
        inv.message?.let {
            Text(
                it,
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.65f),
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
            Text(
                "接受",
                fontSize = 10.sp,
                color = FluentColors.OnAccent,
                modifier = Modifier
                    .background(FluentColors.Accent, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                    .clickable(onClick = onAccept)
                    .padding(horizontal = 10.dp, vertical = 3.dp),
            )
            Text(
                "拒绝",
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                modifier = Modifier
                    .background(MaterialTheme.colorScheme.surfaceContainerHighest, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                    .clickable(onClick = onDecline)
                    .padding(horizontal = 10.dp, vertical = 3.dp),
            )
        }
    }
}

/** 添加好友弹窗：输入 StarcloudID → 搜索 → 发申请 */
@Composable
private fun AddFriendDialog(
    busy: Boolean,
    error: String?,
    onDismiss: () -> Unit,
    onSend: (target: String, message: String?) -> Unit,
    onSearch: suspend (String) -> List<FriendsApi.SearchResult>,
) {
    var query by remember { mutableStateOf("") }
    var message by remember { mutableStateOf("") }
    var results by remember { mutableStateOf<List<FriendsApi.SearchResult>?>(null) }
    var searching by remember { mutableStateOf(false) }
    val scope = androidx.compose.runtime.rememberCoroutineScope()

    Dialog(onDismissRequest = onDismiss) {
        Card(
            Modifier.width(360.dp),
            colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
            ),
            shape = RoundedCornerShape(FluentDimens.CornerRadiusCard),
        ) {
            Column(
                Modifier.fillMaxWidth().padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text("添加好友", fontSize = 15.sp, fontWeight = FontWeight.Medium)
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = query,
                        onValueChange = { query = it },
                        placeholder = { Text("输入对方 StarcloudID 或昵称", fontSize = 11.sp) },
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                    )
                    OutlinedButton(
                        onClick = {
                            if (query.isBlank()) return@OutlinedButton
                            searching = true
                            scope.launch {
                                try {
                                    results = withContext(Dispatchers.IO) { onSearch(query.trim()) }
                                } catch (e: Exception) {
                                    results = emptyList()
                                }
                                searching = false
                            }
                        },
                        enabled = !searching,
                    ) { Text(if (searching) "搜索中" else "搜索", fontSize = 12.sp) }
                }

                val list = results
                if (list != null) {
                    if (list.isEmpty()) {
                        Text(
                            "没有找到匹配的用户",
                            fontSize = 11.sp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.55f),
                        )
                    } else {
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            list.take(5).forEach { r ->
                                Row(
                                    Modifier.fillMaxWidth(),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Avatar(r.profile.displayName.take(1), 18.dp)
                                    Spacer(Modifier.width(8.dp))
                                    Column(Modifier.weight(1f)) {
                                        Text(r.profile.displayName, fontSize = 12.sp, maxLines = 1)
                                        Text(
                                            r.profile.id,
                                            fontSize = 9.sp,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                        )
                                    }
                                    when (r.relation) {
                                        "friend" -> Text(
                                            "已是好友",
                                            fontSize = 10.sp,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                        )
                                        else -> Text(
                                            "发申请",
                                            fontSize = 10.sp,
                                            color = FluentColors.OnAccent,
                                            modifier = Modifier
                                                .background(FluentColors.Accent, RoundedCornerShape(FluentDimens.CornerRadiusSmall))
                                                .clickable(enabled = !busy) {
                                                    onSend(r.profile.userId.ifBlank { r.profile.id }, message.ifBlank { null })
                                                }
                                                .padding(horizontal = 10.dp, vertical = 3.dp),
                                        )
                                    }
                                }
                            }
                        }
                    }
                }

                OutlinedTextField(
                    value = message,
                    onValueChange = { message = it },
                    placeholder = { Text("附言（可选）", fontSize = 11.sp) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                error?.let { Text(it, fontSize = 11.sp, color = FluentColors.Danger) }
                Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                    Text(
                        "关闭",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f),
                        modifier = Modifier
                            .clickable(onClick = onDismiss)
                            .padding(6.dp),
                    )
                }
            }
        }
    }
}

/** 首字符圆形头像（皮肤站头像裁剪随后续接入） */
@Composable
private fun Avatar(first: String, size: androidx.compose.ui.unit.Dp) {
    Box(
        Modifier
            .size(size)
            .background(FluentColors.Accent.copy(alpha = 0.85f), CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            first.ifBlank { "?" },
            fontSize = 10.sp,
            color = FluentColors.OnAccent,
            fontWeight = FontWeight.Medium,
        )
    }
}

private fun fmtTime(iso: String): String = try {
    val t = OffsetDateTime.parse(iso)
    val today = java.time.LocalDate.now()
    if (t.toLocalDate() == today) t.format(DateTimeFormatter.ofPattern("HH:mm"))
    else t.format(DateTimeFormatter.ofPattern("MM/dd HH:mm"))
} catch (e: Exception) {
    iso
}
