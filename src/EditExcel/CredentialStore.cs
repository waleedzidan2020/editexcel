using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace EditExcel;

public static class CredentialStore
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditExcel");
    static readonly string FileName = Path.Combine(Folder, "credential.dpapi");
    public static void Save(LoginCredential credential)
    {
        Directory.CreateDirectory(Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credential);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FileName + ".tmp", encrypted);
            File.Move(FileName + ".tmp", FileName, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static LoginCredential? Load()
    {
        if (!File.Exists(FileName)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FileName), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<LoginCredential>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Delete()
    {
        File.Delete(FileName); File.Delete(FileName + ".tmp");
    }
    public static string LoadClientId() => File.Exists(Path.Combine(Folder, "client-id.txt")) ? File.ReadAllText(Path.Combine(Folder, "client-id.txt")) : "";
    public static void SaveClientId(string value) { Directory.CreateDirectory(Folder); File.WriteAllText(Path.Combine(Folder, "client-id.txt"), value); }
}
