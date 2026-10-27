#!/usr/bin/env python3
"""给自更新清单（latest.json）签 ECDSA P-256 签名。

用法：python tools/sign-manifest.py <latest.json 路径> [私钥 PEM 路径]
输出：同目录 <原名>.sig（base64 签名，客户端拉取后验签）。

签名对象：清单文件的原始字节（去掉 UTF-8 BOM 后的内容，与客户端解析口径一致）。
私钥必须已经存在（secrets/update-signing.key）；缺失时直接报错退出——
自动生成新密钥会让客户端内置的公钥失效，绝不允许静默重建。
"""
import base64
import sys
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_KEY = ROOT / "secrets" / "update-signing.key"


def load_key(path: Path) -> ec.EllipticCurvePrivateKey:
    if not path.exists():
        print(f"[sign] 私钥不存在：{path}。请从备份恢复，不要重新生成——"
              "重新生成的密钥与客户端内置公钥不匹配，所有用户更新都会失败。")
        sys.exit(3)
    return serialization.load_pem_private_key(path.read_bytes(), password=None)


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: sign-manifest.py <manifest.json> [key.pem]")
        return 2
    manifest = Path(sys.argv[1])
    key_path = Path(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_KEY

    raw = manifest.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]

    key = load_key(key_path)
    signature = base64.b64encode(key.sign(raw, ec.ECDSA(hashes.SHA256()))).decode()
    sig_path = manifest.with_suffix(manifest.suffix + ".sig")
    sig_path.write_text(signature + "\n", encoding="ascii")
    print(f"[sign] {manifest.name} -> {sig_path.name} ({len(signature)} chars)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
