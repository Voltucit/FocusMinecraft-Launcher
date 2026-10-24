using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace FMLauncher.Infrastructure.Persistence;

/// <summary>当前 Windows 用户 DPAPI 凭据；设置、主题导出及日志不包含这些值。</summary>
public sealed class SecretStore(string directory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FMLauncher.secrets.v2");
    private string PathFor(string name) => Path.Combine(directory, name is "wallpaper" or "curseforge" ? name + ".dat" : throw new ArgumentException("未知凭据类型。"));
    public string Read(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return "";
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Write(string name, string value)
    {
        var path = PathFor(name); Directory.CreateDirectory(directory);
        if (string.IsNullOrEmpty(value)) { if (File.Exists(path)) File.Delete(path); return; }
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temp = path + ".tmp";
            try { File.WriteAllBytes(temp, cipher); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

