using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace EditExcel;
public partial class MainWindow : Window
{
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditExcel", "Sessions");
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    GitHubClient? client;
    SyncSession? session;
    bool busy;
    string lastMessage = "";
    DateTime retryAt;
    LoginCredential? credential;
    CancellationTokenSource? loginCancellation;
    public MainWindow()
    {
        InitializeComponent(); Directory.CreateDirectory(root);
        timer.Tick += async (_, _) => { if (DateTime.UtcNow >= retryAt) await SyncNow(); };
        try
        {
            ClientIdBox.Text = CredentialStore.LoadClientId();
            credential = CredentialStore.Load();
            if (credential is { Expired: false }) { TokenBox.Password = credential.Token; Report("تم استعادة الدخول المحفوظ بحماية Windows."); }
            else if (credential != null) Report("انتهت صلاحية الدخول المحفوظ. سجّل الدخول من جديد؛ ملفاتك محفوظة.");
        }
        catch { Report("تعذّر قراءة الدخول المحفوظ. يمكنك تسجيل الدخول مجددًا أو نسيان الدخول القديم."); }
    }
    void Report(string text)
    {
        StatusText.Text = text;
        if (text == lastMessage) return;
        lastMessage = text; LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n"); LogBox.ScrollToEnd();
    }
    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || loginCancellation != null) return;
        busy = true; ConnectButton.IsEnabled = false;
        try
        {
            if (TokenBox.Password.Trim().Length == 0) throw new InvalidOperationException("أدخل GitHub Token بصلاحية كتابة على المستودع.");
            if (credential?.Token != TokenBox.Password.Trim()) credential = new(TokenBox.Password.Trim(), null, "");
            if (credential.Expired) throw new InvalidOperationException("انتهت صلاحية الدخول. أعد تسجيل الدخول من المتصفح.");
            var target = RepoFile.Parse(UrlBox.Text, BranchBox.Text);
            Report("جاري تنزيل الملف وفحص النسخة المحلية...");
            client?.Dispose(); client = new GitHubClient(TokenBox.Password);
            session = await SyncSession.Open(client, target, root);
            try { if (RememberBox.IsChecked == true) CredentialStore.Save(credential); else CredentialStore.Delete(); }
            catch { MessageBox.Show("الاتصال نجح لكن تعذّر حفظ الدخول على الجهاز. سيعمل في هذه الجلسة فقط."); }
            OpenExcel(session.LocalPath);
            ConnectionPanel.IsEnabled = false;
            SyncButton.IsEnabled = StopButton.IsEnabled = true;
            retryAt = DateTime.MinValue; timer.Start();
            Report("Excel مفتوح. اضغط Ctrl + S وانتظر تأكيد الرفع هنا.");
        }
        catch (Exception ex) { Report(ex.Message); session = null; client?.Dispose(); client = null; ConnectButton.IsEnabled = true; }
        finally { busy = false; }
    }
    static void OpenExcel(string path)
    {
        string? exe = null;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe");
            var candidate = (key?.GetValue(null) as string)?.Trim('"');
            if (candidate != null && File.Exists(candidate)) exe = candidate;
        }
        if (exe == null) throw new InvalidOperationException("لم أجد Microsoft Excel مثبتًا. ثبّته أو أصلح تثبيت Office ثم أعد المحاولة. النسخة المحلية محفوظة.");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false }; start.ArgumentList.Add(path); Process.Start(start);
    }
    async Task SyncNow()
    {
        if (busy || session == null || session.Paused) return;
        busy = true;
        try { if (credential?.Expired == true) throw new HttpRequestException("انتهت صلاحية الدخول.", null, HttpStatusCode.Unauthorized); var message = await session.Sync(); if (message != null) Report(message); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            timer.Stop(); client?.Dispose(); client = null; session = null;
            SyncButton.IsEnabled = StopButton.IsEnabled = false;
            ConnectionPanel.IsEnabled = ConnectButton.IsEnabled = true;
            Report("انتهت صلاحية الدخول أو تم إلغاؤه. احفظ وأغلق Excel ثم سجّل الدخول وأعد الاتصال. تعديلاتك المحلية محفوظة.");
        }
        catch (ConflictException ex) { timer.Stop(); SyncButton.IsEnabled = false; Report(ex.Message); }
        catch (Exception ex) { retryAt = DateTime.UtcNow.AddSeconds(30); Report(ex.Message + " — إعادة المحاولة بعد 30 ثانية؛ النسخة المحلية محفوظة."); }
        finally { busy = false; }
    }
    async void Sync_Click(object sender, RoutedEventArgs e) => await SyncNow();
    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { Report("انتظر انتهاء العملية الحالية ثم أوقف المزامنة."); return; }
        if (MessageBox.Show("سيتم إيقاف الرفع. احفظ وأغلق ملف Excel قبل الاتصال من جديد. هل تريد المتابعة؟", "إيقاف المزامنة", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        timer.Stop(); session = null; client?.Dispose(); client = null;
        ConnectionPanel.IsEnabled = ConnectButton.IsEnabled = true;
        SyncButton.IsEnabled = StopButton.IsEnabled = false; Report("المزامنة متوقفة. النسخة المحلية محفوظة.");
    }
    void Folder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", session == null ? root : Path.GetDirectoryName(session.LocalPath)!) { UseShellExecute = true });
    void Token_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/settings/personal-access-tokens/new") { UseShellExecute = true });
    async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session != null || loginCancellation != null) return;
        loginCancellation = new(); LoginButton.IsEnabled = ConnectButton.IsEnabled = false; CancelLoginButton.IsEnabled = true;
        var id = ClientIdBox.Text.Trim();
        try
        {
            using var login = new DeviceLogin();
            var result = await login.SignIn(id, code => {
                DeviceCodeBox.Text = code;
                Report("اكتب الكود الظاهر في صفحة GitHub ووافق على تسجيل الدخول.");
                try { Process.Start(new ProcessStartInfo("https://github.com/login/device") { UseShellExecute = true }); }
                catch { Report("افتح https://github.com/login/device واكتب الكود الظاهر."); }
            }, loginCancellation.Token);
            credential = result; TokenBox.Password = result.Token;
            CredentialStore.SaveClientId(id);
            if (RememberBox.IsChecked == true) CredentialStore.Save(result); else CredentialStore.Delete();
            Report("تم تسجيل الدخول. اضغط اتصال وفتح Excel. تأكد أن التطبيق مثبّت على الريبو المطلوب.");
        }
        catch (OperationCanceledException) { Report("تم إلغاء تسجيل الدخول."); }
        catch (Exception ex) { Report(ex.Message); }
        finally { loginCancellation.Dispose(); loginCancellation = null; LoginButton.IsEnabled = ConnectButton.IsEnabled = true; CancelLoginButton.IsEnabled = false; DeviceCodeBox.Clear(); }
    }
    void CancelLogin_Click(object sender, RoutedEventArgs e) => loginCancellation?.Cancel();
    void Remember_Unchecked(object sender, RoutedEventArgs e)
    {
        try { CredentialStore.Delete(); } catch { MessageBox.Show("تعذّر حذف الدخول المحفوظ. جرّب زر نسيان الدخول."); }
    }
    void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session != null || loginCancellation != null) return;
        try { CredentialStore.Delete(); credential = null; TokenBox.Clear(); Report("تم حذف الدخول من هذا الجهاز. لإلغاء تفويض التطبيق نفسه استخدم إعدادات GitHub."); }
        catch { Report("تعذّر حذف الدخول المحفوظ من الجهاز."); }
    }
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (loginCancellation != null) { loginCancellation.Cancel(); e.Cancel = true; Report("جاري إلغاء الدخول؛ أغلق النافذة بعد انتهاء الإلغاء."); return; }
        if (busy) { e.Cancel = true; Report("انتظر اكتمال العملية الحالية قبل الإغلاق."); return; }
        if (session != null && MessageBox.Show("إغلاق البرنامج يوقف المزامنة. تأكد من حفظ Excel وظهور تأكيد الرفع. هل تريد الإغلاق؟", "EditExcel", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        timer.Stop(); client?.Dispose();
    }
}
