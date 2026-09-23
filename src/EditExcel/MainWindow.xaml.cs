using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

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
    public MainWindow()
    {
        InitializeComponent(); Directory.CreateDirectory(root);
        timer.Tick += async (_, _) => { if (DateTime.UtcNow >= retryAt) await SyncNow(); };
    }
    void Report(string text)
    {
        StatusText.Text = text;
        if (text == lastMessage) return;
        lastMessage = text; LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n"); LogBox.ScrollToEnd();
    }
    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true; ConnectButton.IsEnabled = false;
        try
        {
            if (TokenBox.Password.Trim().Length == 0) throw new InvalidOperationException("أدخل GitHub Token بصلاحية كتابة على المستودع.");
            var target = RepoFile.Parse(UrlBox.Text, BranchBox.Text);
            Report("جاري تنزيل الملف وفحص النسخة المحلية...");
            client?.Dispose(); client = new GitHubClient(TokenBox.Password);
            session = await SyncSession.Open(client, target, root);
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
        try { var message = await session.Sync(); if (message != null) Report(message); }
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
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Report("انتظر اكتمال العملية الحالية قبل الإغلاق."); return; }
        if (session != null && MessageBox.Show("إغلاق البرنامج يوقف المزامنة. تأكد من حفظ Excel وظهور تأكيد الرفع. هل تريد الإغلاق؟", "EditExcel", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        timer.Stop(); client?.Dispose();
    }
}
