// 内存中的敏感数据加解密。
//
// Windows 走 CryptProtectMemory（crypt32，按进程会话密钥，内存块级加密）：
// 长期驻留在托管堆里的是密文，取用时才短暂解出明文；非 Windows 平台退化为
// Base64 编码，仅保证编译与单元测试可跑（这些平台本身没有对应的内存保护等价物）。
namespace Starcloud.Core.Services;

internal static class SecureMemory
{
    // CryptProtectMemory 要求数据长度按 32 字节对齐，不足部分补零；
    // 密文前 4 字节存原始长度，解密后按长度截断。
    private const int BlockSize = 32;
    private const uint SameProcess = 0;

    public static byte[] Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return [];
        var raw = System.Text.Encoding.UTF8.GetBytes(plain);
        var padded = new byte[((raw.Length + 4 + BlockSize - 1) / BlockSize) * BlockSize];
        BitConverter.TryWriteBytes(padded.AsSpan(0, 4), raw.Length);
        raw.AsSpan().CopyTo(padded.AsSpan(4));
        Array.Clear(raw);
        if (!OperatingSystem.IsWindows()) return padded;
        if (!CryptProtectMemory(padded, (uint)padded.Length, SameProcess))
            throw new InvalidOperationException(
                $"CryptProtectMemory 失败：Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return padded;
    }

    public static string Unprotect(byte[] cipher)
    {
        if (cipher is not { Length: > 0 }) return "";
        var work = (byte[])cipher.Clone();
        try
        {
            if (OperatingSystem.IsWindows() &&
                !CryptUnprotectMemory(work, (uint)work.Length, SameProcess))
            {
                return "";
            }
            var length = BitConverter.ToInt32(work, 0);
            if (length <= 0 || length > work.Length - 4) return "";
            return System.Text.Encoding.UTF8.GetString(work, 4, length);
        }
        finally
        {
            Array.Clear(work);
        }
    }

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectMemory(byte[] pDataIn, uint cbDataIn, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectMemory(byte[] pDataIn, uint cbDataIn, uint dwFlags);
}

/// <summary>
/// 一个以密文形式驻留内存的令牌（access token / refresh token 等）。
/// 只保存 CryptProtectMemory 后的字节；<see cref="Reveal"/> 每次短暂解出明文，
/// 调用方应尽量在最小作用域内用掉，不要长期持有返回值。
/// </summary>
public sealed class SecureToken : IDisposable
{
    private byte[]? _cipher;
    private int _length;

    public SecureToken(string? plain)
    {
        _cipher = SecureMemory.Protect(plain ?? "");
        _length = plain?.Length ?? 0;
    }

    public bool HasValue => _length > 0 && _cipher is { Length: > 0 };

    /// <summary>明文长度（不触发解密，供判空/日志脱敏使用）</summary>
    public int Length => _length;

    /// <summary>解出明文；对象已释放或无内容时返回空串</summary>
    public string Reveal()
    {
        var cipher = _cipher;
        return cipher is null ? "" : SecureMemory.Unprotect(cipher);
    }

    public void Dispose()
    {
        if (_cipher is { } c) Array.Clear(c);
        _cipher = null;
        _length = 0;
    }
}
