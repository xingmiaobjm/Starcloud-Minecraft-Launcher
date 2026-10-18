/// 账户页：离线 / 微软设备码 / 第三方 Yggdrasil 三类身份。
///
/// 账户与内存会话都在 C#（token 不落盘）；Dart 只做展示与登录流程编排。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';

class AccountsPage extends ConsumerStatefulWidget {
  const AccountsPage({super.key});

  @override
  ConsumerState<AccountsPage> createState() => _AccountsPageState();
}

class _AccountsPageState extends ConsumerState<AccountsPage> {
  List<GameAccount> _accounts = const [];
  String? _current;
  String? _message;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    if (!mounted) return;
    final store = ref.read(kernelDepsProvider).accounts;
    setState(() {
      _accounts = store.accounts();
      _current = store.current()?.uuid;
    });
  }

  Future<void> _run(Future<void> Function(AccountStore store) action) async {
    final store = ref.read(kernelDepsProvider).accounts;
    try {
      await action(store);
      if (!mounted) return;
      setState(() => _message = null);
      _reload();
    } catch (e) {
      if (!mounted) return;
      setState(() => _message = '$e');
    }
  }

  Future<void> _add() async {
    final added = await showDialog<bool>(
      context: context,
      builder: (context) => const _AddAccountDialog(),
    );
    if (added == true && mounted) {
      setState(() => _message = '账户已添加');
      _reload();
    }
  }

  Future<void> _confirmRemove(GameAccount account) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ContentDialog(
        title: Text('移除账户 ${account.name}？'),
        content: const Text('只删除本地保存的身份信息；不会影响服务器上的角色数据。'),
        actions: [
          Button(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('移除'),
          ),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      await _run((store) => store.remove(account.uuid));
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('账户'),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.add),
              label: const Text('添加账户'),
              onPressed: _add,
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (_message != null) ...[
              InfoBar(
                title: Text(_message!),
                severity: InfoBarSeverity.info,
                isLong: true,
                onClose: () => setState(() => _message = null),
              ),
              const SizedBox(height: 12),
            ],
            if (_accounts.isEmpty)
              Expanded(
                child: Center(
                  child: Text(
                    '还没有账户。添加微软正版、第三方（外置登录）或离线账户即可启动游戏。',
                    style: typography.body,
                  ),
                ),
              )
            else
              Expanded(
                child: ListView.builder(
                  itemCount: _accounts.length,
                  itemBuilder: (context, index) =>
                      _accountCard(_accounts[index], typography),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Widget _accountCard(GameAccount account, Typography typography) {
    final isCurrent = _current == account.uuid;
    final expired = account.kind != AccountKind.offline && !account.hasSession;
    final kindLabel = switch (account.kind) {
      AccountKind.offline => '离线',
      AccountKind.microsoft => '微软正版',
      AccountKind.external =>
        account.serverName?.isNotEmpty == true ? account.serverName! : '第三方',
    };
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Card(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(children: [
          Icon(switch (account.kind) {
            AccountKind.offline => FluentIcons.player_settings,
            AccountKind.microsoft => FluentIcons.account_management,
            AccountKind.external => FluentIcons.cloud,
          }),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Wrap(
                  crossAxisAlignment: WrapCrossAlignment.center,
                  spacing: 8,
                  runSpacing: 2,
                  children: [
                    Text(account.name, style: typography.bodyStrong),
                    Text('[$kindLabel]', style: typography.caption),
                    if (isCurrent) Text('[当前]', style: typography.caption),
                    if (expired)
                      Text('[凭证已过期]',
                          style: typography.caption
                              ?.copyWith(color: Colors.orange)),
                  ],
                ),
                Text(account.uuid, style: typography.caption),
                if (account.serverAlias != null)
                  Text('认证服务器：${account.serverAlias}',
                      style: typography.caption),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Button(
            onPressed: isCurrent
                ? null
                : () => _run((store) => store.setCurrent(account.uuid)),
            child: const Text('设为当前'),
          ),
          const SizedBox(width: 8),
          Button(
            onPressed: () => _confirmRemove(account),
            child: const Text('移除'),
          ),
        ]),
      ),
    );
  }
}

/// 添加账户对话框：三种身份共用一套提交入口。
class _AddAccountDialog extends ConsumerStatefulWidget {
  const _AddAccountDialog();

  @override
  ConsumerState<_AddAccountDialog> createState() => _AddAccountDialogState();
}

class _AddAccountDialogState extends ConsumerState<_AddAccountDialog> {
  AccountKind _kind = AccountKind.offline;
  final _name = TextEditingController();
  final _url = TextEditingController();
  final _externalName = TextEditingController();
  final _password = TextEditingController();
  bool _busy = false;
  bool _waiting = false;
  String? _error;
  MicrosoftDeviceCode? _code;

  @override
  void dispose() {
    _name.dispose();
    _url.dispose();
    _externalName.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    switch (_kind) {
      case AccountKind.offline:
        await _addOffline();
      case AccountKind.microsoft:
        await _startMicrosoft();
      case AccountKind.external:
        await _loginExternal();
    }
  }

  Future<void> _addOffline() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref.read(kernelDepsProvider).accounts.addOffline(_name.text);
      if (!mounted) return;
      Navigator.pop(context, true);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _startMicrosoft() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final auth = ref.read(kernelDepsProvider).auth;
      final code = await auth.startMicrosoftLogin();
      if (!mounted) return;
      setState(() {
        _code = code;
        _busy = false;
        _waiting = true;
      });
      while (mounted && _waiting) {
        await Future<void>.delayed(Duration(seconds: code.interval));
        if (!mounted || !_waiting) return;
        final ok = await auth.pollMicrosoftLogin(code);
        if (!mounted) return;
        if (ok) {
          Navigator.pop(context, true);
          return;
        }
      }
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _waiting = false;
        _error = '$e';
      });
    }
  }

  Future<void> _loginExternal() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref.read(kernelDepsProvider).auth.loginExternal(
            url: _url.text.trim(),
            username: _externalName.text.trim(),
            password: _password.text,
          );
      if (!mounted) return;
      _password.clear();
      Navigator.pop(context, true);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ContentDialog(
      title: const Text('添加账户'),
      content: SizedBox(
        width: 440,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            ComboBox<AccountKind>(
              value: _kind,
              items: const [
                ComboBoxItem(
                    value: AccountKind.offline, child: Text('离线账户')),
                ComboBoxItem(
                    value: AccountKind.microsoft, child: Text('微软正版（设备码）')),
                ComboBoxItem(
                    value: AccountKind.external,
                    child: Text('第三方（外置登录）')),
              ],
              onChanged: _waiting
                  ? null
                  : (v) => setState(() {
                        _kind = v ?? AccountKind.offline;
                        _error = null;
                      }),
            ),
            const SizedBox(height: 12),
            if (_kind == AccountKind.offline)
              TextBox(
                controller: _name,
                placeholder: '玩家名（3-16 位字母、数字或下划线）',
                onSubmitted: (_) => unawaited(_submit()),
              ),
            if (_kind == AccountKind.microsoft) ...[
              if (_code == null)
                Text('点击「开始登录」后会显示一次性验证码。', style: typography.body)
              else ...[
                Text('验证码', style: typography.caption),
                SelectableText(
                  _code!.userCode,
                  style: typography.title?.copyWith(letterSpacing: 4),
                ),
                const SizedBox(height: 4),
                Text('在浏览器打开 ${_code!.verificationUri} 并输入上面的验证码。',
                    style: typography.body),
                const SizedBox(height: 8),
                Row(children: [
                  const ProgressRing(strokeWidth: 2),
                  const SizedBox(width: 10),
                  Text(_waiting ? '等待授权…' : '已停止等待', style: typography.caption),
                ]),
              ],
            ],
            if (_kind == AccountKind.external) ...[
              TextBox(
                controller: _url,
                placeholder: '认证服务器地址（如 https://littleskin.cn/api/yggdrasil）',
              ),
              const SizedBox(height: 8),
              TextBox(
                controller: _externalName,
                placeholder: '用户名 / 邮箱',
              ),
              const SizedBox(height: 8),
              TextBox(
                controller: _password,
                placeholder: '密码（只用于本次登录，不落盘）',
                obscureText: true,
              ),
            ],
            if (_error != null) ...[
              const SizedBox(height: 12),
              InfoBar(
                title: const Text('操作失败'),
                content: Text(_error!),
                severity: InfoBarSeverity.error,
                isLong: true,
              ),
            ],
          ],
        ),
      ),
      actions: [
        Button(
          onPressed: () {
            _waiting = false;
            Navigator.pop(context, false);
          },
          child: const Text('取消'),
        ),
        FilledButton(
          onPressed: _busy || _waiting ? null : _submit,
          child: Text(switch (_kind) {
            AccountKind.offline => _busy ? '添加中…' : '添加',
            AccountKind.microsoft => _busy ? '获取验证码…' : '开始登录',
            AccountKind.external => _busy ? '登录中…' : '登录',
          }),
        ),
      ],
    );
  }
}
