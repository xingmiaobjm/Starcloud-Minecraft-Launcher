/// 账户详情：账户信息 + 皮肤管理。
///
/// 离线/微软账户用本地皮肤库（导入/指定/取消/型号覆盖/删除）；
/// 第三方账户用皮肤站衣柜（列表 + 应用）。数据与文件都在 C# 侧。
library;

import 'dart:async';
import 'dart:io' as io;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';
import '../../../contracts/skins.dart';

class AccountDetailPage extends ConsumerStatefulWidget {
  const AccountDetailPage({
    super.key,
    required this.account,
    required this.onBack,
  });

  final GameAccount account;
  final VoidCallback onBack;

  @override
  ConsumerState<AccountDetailPage> createState() => _AccountDetailPageState();
}

class _AccountDetailPageState extends ConsumerState<AccountDetailPage> {
  late GameAccount _account = widget.account;
  List<SkinEntry> _skins = const [];
  List<StationSkin> _station = const [];
  bool _loading = true;
  String? _error;
  String? _notice;
  String? _busy;
  String _model = SkinModels.auto;
  final _path = TextEditingController();
  final _url = TextEditingController();

  SkinManager get _manager => ref.read(kernelDepsProvider).skins;

  bool get _supportsLocalSkins =>
      _account.kind == AccountKind.offline ||
      _account.kind == AccountKind.microsoft;

  @override
  void initState() {
    super.initState();
    _model = _account.skinModel ?? SkinModels.auto;
    unawaited(_load());
  }

  @override
  void dispose() {
    _path.dispose();
    _url.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final skins = await _manager.list();
      var station = const <StationSkin>[];
      if (_account.kind == AccountKind.external) {
        station = await _manager.stationList(_account.uuid);
      }
      if (!mounted) return;
      setState(() {
        _skins = skins;
        _station = station;
        _loading = false;
        _error = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '$e';
      });
    }
  }

  /// 账户缓存由 SkinManager 在指定皮肤后刷新，这里把最新值同步回页面。
  void _syncAccount() {
    final store = ref.read(kernelDepsProvider).accounts;
    for (final account in store.accounts()) {
      if (account.uuid == _account.uuid) {
        _account = account;
        _model = account.skinModel ?? SkinModels.auto;
        return;
      }
    }
  }

  Future<void> _run(String label, Future<void> Function() action,
      {String? success}) async {
    setState(() {
      _busy = label;
      _error = null;
      _notice = null;
    });
    try {
      await action();
      if (!mounted) return;
      _syncAccount();
      setState(() {
        if (success != null) _notice = success;
      });
      await _load();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = null);
    }
  }

  Future<void> _use(SkinEntry skin) => _run(
        '应用皮肤',
        () => _manager.assignToAccount(_account.uuid, skin.id, model: _model),
        success: '已应用「${skin.name}」',
      );

  Future<void> _clearSkin() => _run(
        '取消皮肤',
        () => _manager.assignToAccount(_account.uuid, null),
        success: '已取消账户皮肤',
      );

  Future<void> _saveModel() async {
    if (_account.skinId == null) {
      setState(() => _error = '先选择一张皮肤，再设置型号覆盖');
      return;
    }
    await _run(
      '保存型号',
      () => _manager.assignToAccount(
        _account.uuid,
        _account.skinId,
        model: _model,
      ),
      success: '型号已保存（$_model）',
    );
  }

  Future<void> _deleteSkin(SkinEntry skin) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ContentDialog(
        title: Text('删除皮肤「${skin.name}」？'),
        content: const Text('皮肤文件会从本地皮肤库永久删除；引用它的账户会自动取消指定。'),
        actions: [
          Button(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('删除'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    await _run('删除皮肤', () => _manager.delete(skin.id), success: '皮肤已删除');
  }

  Future<void> _importPath() async {
    final path = _path.text.trim();
    if (path.isEmpty) return;
    await _run(
      '导入皮肤',
      () async {
        await _manager.importPath(path);
        _path.clear();
      },
      success: '已导入本地皮肤',
    );
  }

  Future<void> _importUrl() async {
    final url = _url.text.trim();
    if (url.isEmpty) return;
    await _run(
      '下载皮肤',
      () async {
        await _manager.importUrl(url);
        _url.clear();
      },
      success: '已从链接导入皮肤',
    );
  }

  Future<void> _applyStation(StationSkin skin) => _run(
        '应用皮肤站皮肤',
        () async {
          final message = await _manager.stationApply(_account.uuid, skin.id);
          _notice = message;
        },
      );

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ScaffoldPage(
      header: PageHeader(
        title: Row(children: [
          IconButton(
            icon: const Icon(FluentIcons.back),
            onPressed: widget.onBack,
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              '账户详情 · ${_account.name}',
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ]),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.refresh),
              label: const Text('刷新'),
              onPressed: _loading ? null : _load,
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
        child: ListView(children: [
          if (_error != null) ...[
            InfoBar(
              title: const Text('操作失败'),
              content: Text(_error!),
              severity: InfoBarSeverity.error,
              isLong: true,
              onClose: () => setState(() => _error = null),
            ),
            const SizedBox(height: 12),
          ],
          if (_notice != null) ...[
            InfoBar(
              title: Text(_notice!),
              severity: InfoBarSeverity.success,
              isLong: true,
              onClose: () => setState(() => _notice = null),
            ),
            const SizedBox(height: 12),
          ],
          Card(
            padding: const EdgeInsets.all(16),
            child: Wrap(
              spacing: 24,
              runSpacing: 8,
              children: [
                _info(typography, '账户类型', switch (_account.kind) {
                  AccountKind.offline => '离线',
                  AccountKind.microsoft => '微软正版',
                  AccountKind.external => '第三方（外置登录）',
                }),
                _info(typography, 'UUID', _account.uuid),
                if (_account.serverName != null)
                  _info(typography, '认证服务器', _account.serverName!),
                _info(
                  typography,
                  '凭证',
                  _account.kind == AccountKind.offline
                      ? '离线身份（无需凭证）'
                      : (_account.hasSession ? '本次会话有效' : '已过期，请重新登录'),
                ),
              ],
            ),
          ),
          const SizedBox(height: 12),
          if (_loading)
            const Padding(
              padding: EdgeInsets.all(32),
              child: Center(child: ProgressRing()),
            )
          else if (_supportsLocalSkins)
            ..._localSkinSections(typography)
          else
            ..._stationSkinSections(typography),
        ]),
      ),
    );
  }

  Widget _info(Typography typography, String label, String value) {
    return SizedBox(
      width: 260,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: typography.caption),
          Text(value, style: typography.body),
        ],
      ),
    );
  }

  List<Widget> _localSkinSections(Typography typography) {
    SkinEntry? current;
    for (final skin in _skins) {
      if (skin.id == _account.skinId) {
        current = skin;
        break;
      }
    }
    return [
      Card(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('当前皮肤', style: typography.subtitle),
            const SizedBox(height: 12),
            Row(children: [
              _localSkinImage(current?.path, size: 96),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(current?.name ?? '未指定皮肤',
                        style: typography.bodyStrong),
                    Text(
                      current == null
                          ? '离线/正版账户可使用本地皮肤库里的皮肤（本机注入生效）。'
                          : '型号：${current.model}',
                      style: typography.caption,
                    ),
                    const SizedBox(height: 8),
                    Row(children: [
                      InfoLabel(
                        label: '型号覆盖',
                        child: ComboBox<String>(
                          value: _model,
                          items: const [
                            ComboBoxItem(
                                value: SkinModels.auto, child: Text('自动')),
                            ComboBoxItem(
                                value: SkinModels.classic, child: Text('经典')),
                            ComboBoxItem(
                                value: SkinModels.slim, child: Text('纤细')),
                          ],
                          onChanged: _busy != null
                              ? null
                              : (v) => setState(
                                  () => _model = v ?? SkinModels.auto),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Button(
                        onPressed: _busy != null ? null : _saveModel,
                        child: const Text('保存型号'),
                      ),
                      const SizedBox(width: 8),
                      Button(
                        onPressed:
                            _busy != null || current == null ? null : _clearSkin,
                        child: const Text('取消皮肤'),
                      ),
                    ]),
                  ],
                ),
              ),
            ]),
          ],
        ),
      ),
      const SizedBox(height: 12),
      Card(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('我的皮肤（${_skins.length}）', style: typography.subtitle),
            const SizedBox(height: 12),
            if (_skins.isEmpty)
              Text('皮肤库还是空的，先在下方导入一张 PNG。', style: typography.body)
            else
              Wrap(
                spacing: 12,
                runSpacing: 12,
                children: [
                  for (final skin in _skins)
                    _skinTile(skin, typography,
                        selected: skin.id == _account.skinId),
                ],
              ),
          ],
        ),
      ),
      const SizedBox(height: 12),
      Card(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('导入皮肤', style: typography.subtitle),
            const SizedBox(height: 8),
            Row(children: [
              Expanded(
                child: TextBox(
                  controller: _path,
                  placeholder: '本地 PNG 路径（64×64 或 64×32）',
                ),
              ),
              const SizedBox(width: 8),
              Button(
                onPressed: _busy != null ? null : _importPath,
                child: const Text('导入本地'),
              ),
            ]),
            const SizedBox(height: 8),
            Row(children: [
              Expanded(
                child: TextBox(
                  controller: _url,
                  placeholder: 'PNG 链接（https://…）',
                ),
              ),
              const SizedBox(width: 8),
              Button(
                onPressed: _busy != null ? null : _importUrl,
                child: const Text('从链接导入'),
              ),
            ]),
          ],
        ),
      ),
    ];
  }

  Widget _skinTile(SkinEntry skin, Typography typography,
      {required bool selected}) {
    return Container(
      width: 150,
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(6),
        border: Border.all(
          color: selected
              ? FluentTheme.of(context).accentColor
              : Colors.transparent,
          width: 2,
        ),
      ),
      child: Column(
        children: [
          _localSkinImage(skin.path, size: 96),
          const SizedBox(height: 6),
          Text(skin.name,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: typography.bodyStrong),
          Text('${skin.width}×${skin.height} · ${skin.model}',
              style: typography.caption),
          const SizedBox(height: 6),
          Row(mainAxisAlignment: MainAxisAlignment.center, children: [
            Button(
              onPressed: _busy != null || selected ? null : () => _use(skin),
              child: Text(selected ? '使用中' : '使用'),
            ),
            const SizedBox(width: 6),
            Button(
              onPressed: _busy != null ? null : () => _deleteSkin(skin),
              child: const Text('删除'),
            ),
          ]),
        ],
      ),
    );
  }

  List<Widget> _stationSkinSections(Typography typography) {
    return [
      Card(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('皮肤站衣柜', style: typography.subtitle),
            const SizedBox(height: 8),
            Text(
              '第三方账户的皮肤由认证服务器管理：点「使用」会直接切换皮肤站上的当前皮肤。',
              style: typography.caption,
            ),
            const SizedBox(height: 12),
            if (_station.isEmpty)
              Text(
                _account.hasSession
                    ? '皮肤站衣柜为空（或该皮肤站不支持衣柜接口）。'
                    : '本次会话未登录该账户，请先在账户页重新登录后再试。',
                style: typography.body,
              )
            else
              Wrap(
                spacing: 12,
                runSpacing: 12,
                children: [
                  for (final skin in _station)
                    Container(
                      width: 150,
                      padding: const EdgeInsets.all(8),
                      child: Column(
                        children: [
                          _stationImage(skin.url, size: 96),
                          const SizedBox(height: 6),
                          Text(skin.name,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: typography.bodyStrong),
                          Text(skin.slim ? '纤细' : '经典',
                              style: typography.caption),
                          const SizedBox(height: 6),
                          Button(
                            onPressed: _busy != null
                                ? null
                                : () => _applyStation(skin),
                            child: const Text('使用'),
                          ),
                        ],
                      ),
                    ),
                ],
              ),
          ],
        ),
      ),
    ];
  }

  Widget _localSkinImage(String? path, {double size = 64}) {
    if (path == null || !io.File(path).existsSync()) {
      return SizedBox(
        width: size,
        height: size,
        child: const Icon(FluentIcons.contact),
      );
    }
    return SizedBox(
      width: size,
      height: size,
      child: Image.file(
        io.File(path),
        fit: BoxFit.contain,
        errorBuilder: (_, _, _) => const Icon(FluentIcons.contact),
      ),
    );
  }

  Widget _stationImage(String? url, {double size = 64}) {
    if (url == null || url.isEmpty) {
      return SizedBox(
        width: size,
        height: size,
        child: const Icon(FluentIcons.contact),
      );
    }
    return SizedBox(
      width: size,
      height: size,
      child: Image.network(
        url,
        fit: BoxFit.contain,
        errorBuilder: (_, _, _) => const Icon(FluentIcons.contact),
      ),
    );
  }
}
