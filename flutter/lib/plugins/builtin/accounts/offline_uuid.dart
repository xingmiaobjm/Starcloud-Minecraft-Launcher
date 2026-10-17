/// 离线账户 UUID（Minecraft 官方算法）：
/// `UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8))`
/// = MD5 摘要，version 位置 3、variant 置 IETF（RFC 4122）。
library;

import 'dart:convert';
import 'package:crypto/crypto.dart' as crypto;

String offlineUuid(String playerName) {
  final digest = crypto.md5.convert(utf8.encode('OfflinePlayer:$playerName'));
  final bytes = digest.bytes.toList();
  bytes[6] = (bytes[6] & 0x0F) | 0x30; // version 3
  bytes[8] = (bytes[8] & 0x3F) | 0x80; // variant 10xx
  final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}
