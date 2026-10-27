using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Starcloud.Core.Tests;

public class ShaProbe3
{
    [Fact]
    public void 输出三方sha到文件()
    {
        const string pubHex = "3059301306072a8648ce3d020106082a8648ce3d030107034200045c6fe9b5e9c0a6da64258033d7d3f3efdec501de742b773dac9cadf338d4a302924df2b11a2f7daa8110d0ba9eca089e09db99f0b002235f54b0677cae120c74";
        const string json = "{\"version\":\"9.9.9\",\"notes\":\"n\",\"setup\":{\"file\":\"a.zip\",\"sha256\":\"00\",\"size\":1}}";
        const string sig = "MEUCIC/5M9m0rY4uTTbEpj24CpIBuf49qzyApljO9aWvrafMAiEA6CfBrVUAS3rxs+B85j3u2lqVenv9TV5tnom0NT1tSCE=";
        var lines = new List<string>
        {
            "json_sha = " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            "pub_sha  = " + Convert.ToHexString(SHA256.HashData(Convert.FromHexString(pubHex))).ToLowerInvariant(),
            "sig_sha  = " + Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(sig))).ToLowerInvariant(),
            "sig_len  = " + Convert.FromBase64String(sig).Length,
        };
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "probe3.txt"), lines);
    }
}
