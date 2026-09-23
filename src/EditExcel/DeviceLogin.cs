using System.Net.Http;
using System.Text.Json;
namespace EditExcel;

public sealed record LoginCredential(string Token, DateTimeOffset? ExpiresAt, string ClientId)
{
    public bool Expired => ExpiresAt is { } time && time <= DateTimeOffset.UtcNow.AddMinutes(1);
}
public sealed class DeviceLogin : IDisposable
{
    readonly HttpClient http;
    readonly Func<TimeSpan, CancellationToken, Task> delay;
    public DeviceLogin(HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EditExcel/1.1");
        this.delay = delay ?? ((time, ct) => Task.Delay(time, ct));
    }
    async Task<JsonDocument> Post(string path, Dictionary<string,string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync("https://github.com/" + path, content, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"تعذّر تسجيل الدخول (HTTP {(int)response.StatusCode}). حاول مرة أخرى.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public async Task<LoginCredential> SignIn(string clientId, Action<string> showCode, CancellationToken ct)
    {
        clientId = clientId.Trim();
        if (clientId.Length == 0) throw new InvalidOperationException("أدخل Client ID لتطبيق GitHub أولًا. خطوات الإعداد في GITHUB-APP-SETUP.md.");
        using var start = await Post("login/device/code", new() { ["client_id"] = clientId }, ct);
        var r = start.RootElement;
        if (r.TryGetProperty("error", out var error)) throw Error(error.GetString());
        var code = r.GetProperty("device_code").GetString()!;
        var interval = Math.Max(1, r.GetProperty("interval").GetInt32());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(r.GetProperty("expires_in").GetInt32()));
        showCode(r.GetProperty("user_code").GetString()!);
        try
        {
            while (true)
            {
                await delay(TimeSpan.FromSeconds(interval), timeout.Token);
                using var response = await Post("login/oauth/access_token", new() { ["client_id"] = clientId, ["device_code"] = code, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code" }, timeout.Token);
                var body = response.RootElement;
                if (body.TryGetProperty("access_token", out var token))
                {
                    if (string.IsNullOrWhiteSpace(token.GetString())) throw new InvalidOperationException("GitHub أعاد بيانات دخول فارغة.");
                    DateTimeOffset? expires = body.TryGetProperty("expires_in", out var seconds) ? DateTimeOffset.UtcNow.AddSeconds(seconds.GetInt32()) : null;
                    return new(token.GetString()!, expires, clientId);
                }
                var reason = body.GetProperty("error").GetString();
                if (reason == "authorization_pending") continue;
                if (reason == "slow_down") { interval = Math.Max(interval + 5, body.TryGetProperty("interval", out var slower) ? slower.GetInt32() : 0); continue; }
                throw Error(reason);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("انتهت مهلة تسجيل الدخول. اضغط تسجيل الدخول للمحاولة من جديد."); }
    }
    static Exception Error(string? reason) => new InvalidOperationException(reason switch {
        "device_flow_disabled" => "فعّل Enable Device Flow في إعدادات GitHub App.",
        "incorrect_client_credentials" => "Client ID غير صحيح. استخدم Client ID وليس App ID أو Client Secret.",
        "access_denied" => "تم رفض تسجيل الدخول من المتصفح.",
        "expired_token" or "token_expired" => "انتهت صلاحية كود الدخول؛ أعد المحاولة.",
        _ => "تعذّر تسجيل الدخول. راجع إعداد GitHub App ثم حاول مجددًا." });
    public void Dispose() => http.Dispose();
}
