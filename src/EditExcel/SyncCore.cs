using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EditExcel;

public sealed record RepoFile(string Owner, string Repo, string Branch, string Path)
{
    public string ApiPath => $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}/contents/{string.Join('/', Path.Split('/').Select(Uri.EscapeDataString))}";
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Owner}/{Repo}/{Branch}/{Path}")))[..24];
    public static RepoFile Parse(string url, string branch)
    {
        branch = branch.Trim();
        if (branch.Length == 0) throw new InvalidOperationException("اكتب اسم الفرع.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            throw new InvalidOperationException("أدخل رابط HTTPS صحيحًا لملف GitHub.");
        var parts = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length < 4) throw new InvalidOperationException("الرابط يجب أن يشير إلى ملف، وليس الريبو فقط.");
        string tail;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && (parts[2] == "blob" || parts[2] == "raw")) tail = string.Join('/', parts.Skip(3));
        else if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) tail = string.Join('/', parts.Skip(2));
        else throw new InvalidOperationException("استخدم رابط github.com/blob أو raw.githubusercontent.com.");
        if (tail.StartsWith("refs/heads/", StringComparison.Ordinal)) tail = tail[11..];
        if (!tail.StartsWith(branch + "/", StringComparison.Ordinal)) throw new InvalidOperationException("اسم الفرع لا يطابق الرابط. عدّل خانة Branch.");
        var path = tail[(branch.Length + 1)..];
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidOperationException("هذه النسخة تدعم ملفات .xlsx فقط.");
        return new(parts[0], parts[1], branch, path);
    }
}

public sealed class ConflictException : Exception
{
    public ConflictException() : base("تعارض: الملف تغيّر على GitHub. توقف الرفع لحماية النسختين. نسختك المحلية محفوظة؛ راجع README لحل التعارض.") { }
}

public static class WorkbookBytes
{
    public const int MaxSize = 20 * 1024 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string GitSha(byte[] bytes) => Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray())).ToLowerInvariant();
    public static void Validate(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxSize) throw new InvalidDataException("الملف فارغ أو أكبر من 20 MB.");
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.GetEntry("[Content_Types].xml") == null || zip.GetEntry("xl/workbook.xml") == null)
            throw new InvalidDataException("الملف ليس XLSX صالحًا أو لم يكتمل حفظه.");
    }
    public static async Task<byte[]> ReadStable(string path)
    {
        static async Task<byte[]> Read(string p)
        {
            using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length > MaxSize) throw new InvalidDataException("الحد الأقصى 20 MB.");
            using var m = new MemoryStream();
            await s.CopyToAsync(m);
            return m.ToArray();
        }
        var first = await Read(path);
        await Task.Delay(1200);
        var second = await Read(path);
        if (!first.AsSpan().SequenceEqual(second)) throw new IOException("Excel ما زال يحفظ الملف؛ سنحاول مرة أخرى.");
        Validate(second);
        return second;
    }
}

public sealed record RemoteFile(string Sha, byte[] Bytes);
public sealed class GitHubClient : IDisposable
{
    readonly HttpClient http;
    public GitHubClient(string token, HttpMessageHandler? handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/"); http.Timeout = TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EditExcel/1.0");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    static void Check(HttpResponseMessage r)
    {
        if (r.StatusCode == HttpStatusCode.Conflict) throw new ConflictException();
        if (r.IsSuccessStatusCode) return;
        var message = (int)r.StatusCode switch {
            401 => "التوكن غير صحيح أو انتهت صلاحيته.",
            403 => "GitHub رفض الطلب: راجع صلاحيات التوكن أو حدود الطلبات وسياسة الفرع.",
            404 => "الملف أو الفرع غير موجود، أو التوكن لا يملك الوصول إليه.",
            422 => "GitHub رفض التحديث. راجع قواعد الفرع وصلاحية الكتابة.",
            _ => "تعذّر الاتصال بخدمة GitHub. ستظل نسختك المحلية محفوظة." };
        throw new HttpRequestException($"{message} (HTTP {(int)r.StatusCode})", null, r.StatusCode);
    }
    public async Task<RemoteFile> Get(RepoFile file)
    {
        using var r = await http.GetAsync(file.ApiPath + "?ref=" + Uri.EscapeDataString(file.Branch));
        Check(r);
        using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = j.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("type").GetString() != "file") throw new InvalidDataException("الرابط ليس ملفًا عاديًا.");
        var sha = root.GetProperty("sha").GetString()!;
        if (root.GetProperty("size").GetInt64() > WorkbookBytes.MaxSize) throw new InvalidDataException("الحد الأقصى 20 MB.");
        byte[] bytes;
        if (root.TryGetProperty("encoding", out var enc) && enc.GetString() == "base64") bytes = Convert.FromBase64String(root.GetProperty("content").GetString()!);
        else
        {
            // Download the exact blob SHA, not a mutable branch or an arbitrary redirect URL.
            using var raw = new HttpRequestMessage(HttpMethod.Get, $"repos/{Uri.EscapeDataString(file.Owner)}/{Uri.EscapeDataString(file.Repo)}/git/blobs/{sha}");
            raw.Headers.Accept.Clear(); raw.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
            using var rr = await http.SendAsync(raw); Check(rr); bytes = await rr.Content.ReadAsByteArrayAsync();
        }
        WorkbookBytes.Validate(bytes);
        if (WorkbookBytes.GitSha(bytes) != sha) throw new InvalidDataException("فشل التحقق من سلامة الملف الذي تم تنزيله.");
        return new(sha, bytes);
    }
    public async Task<string> Put(RepoFile file, byte[] bytes, string expectedSha)
    {
        WorkbookBytes.Validate(bytes);
        using var content = new StringContent(JsonSerializer.Serialize(new { message = $"Update {file.Path} from EditExcel", content = Convert.ToBase64String(bytes), sha = expectedSha, branch = file.Branch }), Encoding.UTF8, "application/json");
        using var r = await http.PutAsync(file.ApiPath, content); Check(r);
        using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return j.RootElement.GetProperty("content").GetProperty("sha").GetString()!;
    }
    public void Dispose() => http.Dispose();
}

public sealed record SessionState(string RemoteSha, string UploadedHash);
public sealed class SyncSession
{
    readonly GitHubClient client;
    readonly RepoFile file;
    readonly string statePath;
    SessionState state;
    public string LocalPath { get; }
    public bool Paused { get; private set; }
    SyncSession(GitHubClient client, RepoFile file, string localPath, string statePath, SessionState state)
    { this.client = client; this.file = file; LocalPath = localPath; this.statePath = statePath; this.state = state; }
    public static async Task<SyncSession> Open(GitHubClient client, RepoFile file, string root)
    {
        var folder = System.IO.Path.Combine(root, file.Key); Directory.CreateDirectory(folder);
        // A fixed safe local name avoids path traversal and Windows reserved-name problems.
        var local = System.IO.Path.Combine(folder, "workbook.xlsx");
        var metadata = System.IO.Path.Combine(folder, "state.json");
        var remote = await client.Get(file);
        SessionState? state = null;
        if (File.Exists(metadata)) state = JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(metadata)) ?? throw new InvalidDataException("بيانات الجلسة تالفة؛ انسخ ملفك المحلي احتياطيًا قبل إعادة الاتصال.");
        if (File.Exists(local))
        {
            var localBytes = await WorkbookBytes.ReadStable(local);
            if (state == null) throw new InvalidOperationException("توجد نسخة محلية بلا سجل مزامنة؛ انقلها لمكان آمن قبل بدء جلسة جديدة.");
            if (WorkbookBytes.Hash(localBytes) != state.UploadedHash)
                return new(client, file, local, metadata, state); // Preserve pending edits, including offline saves.
            if (remote.Sha != state.RemoteSha)
            {
                File.Copy(local, local + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                await File.WriteAllBytesAsync(local, remote.Bytes);
            }
        }
        else await File.WriteAllBytesAsync(local, remote.Bytes);
        var session = new SyncSession(client, file, local, metadata, new(remote.Sha, WorkbookBytes.Hash(remote.Bytes)));
        await session.SaveState();
        return session;
    }
    async Task SaveState()
    {
        await File.WriteAllTextAsync(statePath + ".tmp", JsonSerializer.Serialize(state));
        File.Move(statePath + ".tmp", statePath, true);
    }
    public async Task<string?> Sync()
    {
        if (Paused) return null;
        var bytes = await WorkbookBytes.ReadStable(LocalPath);
        var hash = WorkbookBytes.Hash(bytes);
        if (hash == state.UploadedHash) return null;
        var remote = await client.Get(file);
        // Recover if a prior upload succeeded but its response or state write was lost.
        if (WorkbookBytes.Hash(remote.Bytes) == hash)
        { state = new(remote.Sha, hash); await SaveState(); return "النسخة محفوظة بالفعل على GitHub."; }
        if (remote.Sha != state.RemoteSha) { Paused = true; throw new ConflictException(); }
        try
        {
            var sha = await client.Put(file, bytes, state.RemoteSha);
            state = new(sha, hash); await SaveState();
            return "تم رفع آخر نسخة محفوظة إلى GitHub بنجاح.";
        }
        catch (ConflictException) { Paused = true; throw; }
    }
}
