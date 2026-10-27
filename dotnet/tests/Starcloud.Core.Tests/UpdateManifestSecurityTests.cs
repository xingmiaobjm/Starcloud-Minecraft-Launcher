// 自更新清单安全：ECDSA P-256 验签（含跨语言夹具）+ 产物校验强制化。
//
// 跨语言夹具：tools/sign-manifest.py（Python cryptography）签出的签名，
// 用同一把测试公钥在 C# 端验证——这是「Python 签 → C# 验」唯一自动化的证明。
// 私钥样例随夹具给出，重新生成时需同步更新三个常量。

using System.Security.Cryptography;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class UpdateManifestSecurityTests
{
    /// <summary>跨语言夹具公钥（SPKI DER，hex）——与 fixtures-update-key.pem 同一把</summary>
    private const string FixturePubHex =
        "3059301306072a8648ce3d020106082a8648ce3d0301070342000491a7a07dcedcf3fba1ac7352cf534e0789856d9112400795765e78bdd4d1643df9997badea8137e53a66258cfd9492c3a4c6cbbc1af412fc083e1356f27da272";

    /// <summary>夹具签名的清单原文（Python 端按此字节签名）</summary>
    private const string FixtureJson =
        "{\"version\":\"9.9.9\",\"notes\":\"n\",\"setup\":{\"file\":\"a.zip\",\"sha256\":\"00\",\"size\":1}}";

    /// <summary>夹具签名（base64）</summary>
    private const string FixtureSig =
        "MEUCIC/5M9m0rY4uTTbEpj24CpIBuf49qzyApljO9aWvrafMAiEA6CfBrVUAS3rxs+B85j3u2lqVenv9TV5tnom0NT1tSCE=";

    [Fact]
    public void 跨语言夹具_Python签出的签名C端能验()
    {
        // 不抛 = Python 签出的原始字节，C# 端按同一口径验签通过
        SelfUpdateService.VerifyWithKey(FixturePubHex, FixtureJson, FixtureSig);
    }

    [Fact]
    public void 跨语言夹具_篡改一个字节即拒绝()
    {
        var tampered = FixtureJson.Replace("9.9.9", "9.9.8");
        Assert.Throws<InvalidDataException>(() =>
            SelfUpdateService.VerifyWithKey(FixturePubHex, tampered, FixtureSig));
    }

    [Fact]
    public void 验签_正确签名通过_篡改拒绝()
    {
        var (key, pubHex) = NewKey();

        const string json = """{"version":"9.9.9","notes":"n","setup":{"file":"a.zip","sha256":"00","size":1}}""";
        var sig = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        // 正确签名通过
        SelfUpdateService.VerifyWithKey(pubHex, json, sig);

        // 伪造一字节 → 必须拒绝
        var tampered = json.Replace("9.9.9", "9.9.8");
        var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var badSig = Convert.ToBase64String(key2.SignData(Encoding.UTF8.GetBytes(tampered), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.Throws<InvalidDataException>(() =>
            SelfUpdateService.VerifyWithKey(pubHex, tampered, badSig));
    }

    private static (ECDsa Key, string PubHex) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = key.ExportSubjectPublicKeyInfo();
        return (key, Convert.ToHexString(pub));
    }

    private static string Sign(ECDsa key, string json) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    [Fact]
    public void 验签_正确签名通过_篡改拒绝_自建密钥()
    {
        var (key, pubHex) = NewKey();

        const string json = """{"version":"9.9.9","notes":"n"}""";
        var sig = Sign(key, json);

        SelfUpdateService.VerifyWithKey(pubHex, json, sig);

        var tampered = json.Replace("9.9.9", "9.9.8");
        var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var badSig = Convert.ToBase64String(key2.SignData(Encoding.UTF8.GetBytes(tampered), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.Throws<InvalidDataException>(() =>
            SelfUpdateService.VerifyWithKey(pubHex, tampered, badSig));
    }

    [Fact]
    public void 产物校验_sha256缺失或size非法时拒绝()
    {
        var file = Path.Combine(Path.GetTempPath(), "sc-sec-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllBytes(file, new byte[100]);
        try
        {
            Assert.Throws<InvalidDataException>(() => SelfUpdateService.VerifyArtifact(file,
                new UpdateArtifact("a.zip", "", 100)));            // sha256 空
            Assert.Throws<InvalidDataException>(() => SelfUpdateService.VerifyArtifact(file,
                new UpdateArtifact("a.zip", "abc", 100)));           // sha256 非 64 hex
            Assert.Throws<InvalidDataException>(() => SelfUpdateService.VerifyArtifact(file,
                new UpdateArtifact("a.zip", new string('0', 64), 0))); // size 0
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void 产物校验_合法哈希与大小通过()
    {
        var file = Path.Combine(Path.GetTempPath(), "sc-sec-" + Guid.NewGuid().ToString("N")[..8]);
        var data = new byte[100];
        File.WriteAllBytes(file, data);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        try
        {
            SelfUpdateService.VerifyArtifact(file, new UpdateArtifact("a.zip", sha.ToUpperInvariant(), 100));
        }
        finally { File.Delete(file); }
    }
}
