using System.Windows;
using System.Threading;
namespace EditExcel;
public partial class App : Application
{
    Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, @"Local\EditExcel.SingleInstance", out var created);
        if (!created) { MessageBox.Show("EditExcel مفتوح بالفعل. استخدم النافذة الحالية."); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
