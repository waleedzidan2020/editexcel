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
