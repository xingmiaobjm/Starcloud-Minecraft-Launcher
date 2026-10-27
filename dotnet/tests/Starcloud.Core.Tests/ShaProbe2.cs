using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Starcloud.Core.Tests;

public class ShaProbe2
{
    [Fact]
    public void 打印验签要素到文件()
    {
        const string pubHex = "3059301306072a8648ce3d020106082a8648ce3d030107034200045c6fe9b5e9c0a6da64258033d7d3f3efdec501de742b773dac9cadf338d4a302924df2b11a2f7daa8110d0ba9eca089e09db99f0b002235f54b0677cae120c74";
        const string json = "{\"version\":\"9.9.9\",\"notes\":\"n\",\"setup\":{\"file\":\"a.zip\",\"sha256\":\"00\",\"size\":1}}";
        const string sig = "MEUCIQCcv11oQn2umoaeDS+d/N8xEbdCIgdlRxgESzg81PmYRwIgH2ghREFoJNqWKdayLHS+Fr89lbaj1jCamVjmFd/rhLw=";
        var lines = new List<string>();
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromHexString(pubHex), out _);
        var imported = true;
            lines.Add($"import={imported} read=true");
            var ok = ecdsa.VerifyData(Encoding.UTF8.GetBytes(json), Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
            lines.Add($"verify={ok}");
        }
        catch (Exception ex)
        {
            lines.Add("EXC: " + ex.GetType().Name + " " + ex.Message);
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "probe2.txt"), lines);
    }
}
