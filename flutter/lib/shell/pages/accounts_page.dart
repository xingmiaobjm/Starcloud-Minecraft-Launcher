/// 账户页：离线账户的增/选/删（微软登录后续迭代并入同一页）。
library;

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
  final _name = TextEditingController();
  List<GameAccount> _accounts = const [];
  String? _current;
  String? _message;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    final store = ref.read(kernelDepsProvider).accounts;
    if (!mounted) return;
    setState(() {
      _accounts = store.accounts();
      _current = store.current()?.uuid;
    });
  }

  Future<void> _add() async {
    final store = ref.read(kernelDepsProvider).accounts;
    try {
      await store.addOffline(_name.text);
      if (!mounted) return;
      setState(() {
        _message = '已添加离线账户';
        _name.clear();
      });
      _reload();
    } catch (e) {
      if (!mounted) return;
      setState(() => _message = '$e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ScaffoldPage(
      header: PageHeader(title: const Text('账户')),
      content: ListView(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        children: [
          Card(
            padding: const EdgeInsets.all(16),
            child: Row(children: [
              Expanded(
                child: InfoLabel(
                  label: '离线玩家名（1-16 字符）',
                  child: TextBox(
                    controller: _name,
                    placeholder: '如：Notch',
                    onSubmitted: (_) => _add(),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              FilledButton(onPressed: _add, child: const Text('添加离线账户')),
            ]),
          ),
          if (_message != null)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Text(_message!, style: typography.caption),
            ),
          const SizedBox(height: 12),
          Text('游戏账户', style: typography.subtitle),
          const SizedBox(height: 8),
          for (final a in _accounts)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Card(
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                child: Row(children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(a.name, style: typography.bodyStrong),
                        Text('${a.kind.name} · ${a.uuid}',
                            style: typography.caption),
                      ],
                    ),
                  ),
                  if (_current == a.uuid)
                    const InfoBar(title: Text('当前'), isLong: false)
                  else
                    Button(
                      onPressed: () async {
                        await ref
                            .read(kernelDepsProvider)
                            .accounts
                            .setCurrent(a.uuid);
                        _reload();
                      },
                      child: const Text('设为当前'),
                    ),
                  const SizedBox(width: 8),
                  Button(
                    onPressed: () async {
                      await ref
                          .read(kernelDepsProvider)
                          .accounts
                          .remove(a.uuid);
                      _reload();
                    },
                    child: const Text('移除'),
                  ),
                ]),
              ),
            ),
          if (_accounts.isEmpty)
            Text('还没有账户。添加一个离线账户即可启动单人/局域网游戏。',
                style: typography.body),
        ],
      ),
    );
  }
}
