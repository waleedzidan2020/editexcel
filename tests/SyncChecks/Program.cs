using EditExcel;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static byte[] Book(string value)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
    foreach (var name in new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml" })
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
    return stream.ToArray();
}
var target = RepoFile.Parse("https://github.com/a/b/blob/main/data/test.xlsx", "main");
var waits = new List<double>();
using (var login = new DeviceLogin(new FakeLogin("authorization_pending", "slow_down", "success"), (time, ct) => { ct.ThrowIfCancellationRequested(); waits.Add(time.TotalSeconds); return Task.CompletedTask; }))
{
    var result = await login.SignIn("test-client", code => Assert(code == "ABCD-EFGH", "Visible user code"), CancellationToken.None);
    Assert(result.Token == "test-access" && !result.Expired, "Login result and expiry");
    Assert(waits.SequenceEqual(new double[] { 5, 5, 10 }), "Respect GitHub polling interval and slow_down");
}
using (var denied = new DeviceLogin(new FakeLogin("access_denied"), (_, _) => Task.CompletedTask))
{
    try { await denied.SignIn("test-client", _ => {}, CancellationToken.None); throw new Exception("Denied login accepted"); }
    catch (InvalidOperationException ex) { Assert(ex.Message.Contains("رفض"), "Denied message"); }
}
using (var cancel = new CancellationTokenSource())
using (var login = new DeviceLogin(new FakeLogin("success"), (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }))
{
    try { await login.SignIn("test-client", _ => cancel.Cancel(), cancel.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}
Assert(new LoginCredential("test", DateTimeOffset.UtcNow.AddHours(-1), "id").Expired, "Expired credential");
Console.WriteLine("PASS: device login, pending, slow_down, expiry, denial and cancellation.");
Assert(target.Path == "data/test.xlsx", "Parse path");
Assert(RepoFile.Parse("https://raw.githubusercontent.com/a/b/refs/heads/feature/test/data/a.xlsx", "feature/test").Path == "data/a.xlsx", "Slash branch");
Assert(RepoFile.Parse("https://github.com/a/b/blob/main/%D9%85%D8%AF%D8%B1%D8%B3%D8%A9.xlsx", "main").Path == "مدرسة.xlsx", "Arabic path");
try { RepoFile.Parse("https://evil.example/a/b/blob/main/a.xlsx", "main"); throw new Exception("Invalid host allowed"); } catch (InvalidOperationException) { }
var root = Path.Combine(Path.GetTempPath(), "EditExcelChecks-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var server = new FakeGitHub(Book("initial"));
    using var api = new GitHubClient("fake-test-token", server);
    var s = await SyncSession.Open(api, target, root);
    Assert(await s.Sync() == null && server.Writes == 0, "Unchanged file must not commit");
    var edited = Book("edited"); await File.WriteAllBytesAsync(s.LocalPath, edited);
    await s.Sync(); Assert(server.Writes == 1 && server.Bytes.SequenceEqual(edited), "Saved edit uploaded");
    await s.Sync(); Assert(server.Writes == 1, "No duplicate commit");
    var pending = Book("pending"); await File.WriteAllBytesAsync(s.LocalPath, pending);
    s = await SyncSession.Open(api, target, root);
    Assert((await File.ReadAllBytesAsync(s.LocalPath)).SequenceEqual(pending), "Restart preserves pending local edit");
    server.Bytes = Book("somebody else");
    try { await s.Sync(); throw new Exception("Conflict was not caught"); } catch (ConflictException) { }
    Assert(s.Paused && server.Writes == 1, "Conflict must pause without overwrite");
    Assert((await File.ReadAllBytesAsync(s.LocalPath)).SequenceEqual(pending), "Conflict preserves local bytes");
    // Lost response recovery: remote already has our exact pending bytes.
    server.Bytes = pending; s = await SyncSession.Open(api, target, root); await s.Sync();
    Assert(server.Writes == 1, "Recovery must not duplicate upload");
    await File.WriteAllBytesAsync(s.LocalPath, Book("race")); server.ConflictOnPut = true;
    try { await s.Sync(); throw new Exception("Race not caught"); } catch (ConflictException) { }
    Assert(s.Paused, "PUT conflict must pause");
    Console.WriteLine("PASS: URL parsing, unchanged files, upload, duplicate prevention, pending restart, remote conflict, lost-response recovery, concurrent PUT conflict.");
}
finally { Directory.Delete(root, true); }

sealed class FakeGitHub(byte[] bytes) : HttpMessageHandler
{
    public byte[] Bytes = bytes;
    public int Writes;
    public bool ConflictOnPut;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
            return Json(new { type = "file", encoding = "base64", size = Bytes.Length, sha = WorkbookBytes.GitSha(Bytes), content = Convert.ToBase64String(Bytes) });
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (ConflictOnPut || body.RootElement.GetProperty("sha").GetString() != WorkbookBytes.GitSha(Bytes)) return new(HttpStatusCode.Conflict);
        Bytes = Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!); Writes++;
        return Json(new { content = new { sha = WorkbookBytes.GitSha(Bytes) } });
    }
    static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
}

sealed class FakeLogin(params string[] responses) : HttpMessageHandler
{
    readonly Queue<string> sequence = new(responses);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        object result;
        if (request.RequestUri!.AbsolutePath.EndsWith("/device/code")) result = new { device_code = "private-device-code", user_code = "ABCD-EFGH", interval = 5, expires_in = 900 };
        else { var next = sequence.Dequeue(); result = next == "success" ? (object)new { access_token = "test-access", expires_in = 28800 } : new { error = next }; }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result)) });
    }
}
