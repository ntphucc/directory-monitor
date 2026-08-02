using System.Security.Cryptography;
using System.Text;

namespace DirectoryMonitor.Infrastructure.Security;

public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Directory Monitor/v1");
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
}
