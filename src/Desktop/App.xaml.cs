using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using RealtimeTranscription.Desktop.Input;

namespace RealtimeTranscription.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls,string title);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UiaWorker.IsWorker(e.Args)) { await Task.Run(UiaWorker.Run); Shutdown(); return; }
        if (DesktopSmoke.IsSmoke(e.Args)) { Shutdown(await DesktopSmoke.RunAsync(e.Args)); return; }
        string user=WindowsIdentity.GetCurrent().User?.Value??Environment.UserName;
        instance=new Mutex(true,"Local\\RealtimeTranscription."+user,out bool created);
        if(!created){var handle=FindWindow(null,"语音输入法");if(handle!=IntPtr.Zero){ShowWindow(handle,9);SetForegroundWindow(handle);}Shutdown();return;}
        DispatcherUnhandledException+=(_,args)=>{args.Handled=true;MessageBox.Show("操作遇到错误。已显示的正文可以继续复制或导出。","语音输入法",MessageBoxButton.OK,MessageBoxImage.Warning);};
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RealtimeTranscription");
        var controller=new AppController(folder);var window=new MainWindow(controller);MainWindow=window;window.Show();
        try{await controller.InitializeAsync();window.Ready();}
        catch{MessageBox.Show("配置载入未完成，请检查设置。本地数据已保留。","启动",MessageBoxButton.OK,MessageBoxImage.Warning);window.Ready();}
    }
    protected override void OnExit(ExitEventArgs e){try{instance?.ReleaseMutex();}catch(ApplicationException){}instance?.Dispose();base.OnExit(e);}
}
