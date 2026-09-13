using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace RealtimeTranscription.Desktop;
public sealed class VoiceOverlay : Window
{
    private readonly TextBlock title,preview;
    private readonly System.Windows.Threading.DispatcherTimer hide=new(){Interval=TimeSpan.FromSeconds(3)};
    public VoiceOverlay()
    {
        Width=610;SizeToContent=SizeToContent.Height;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ShowActivated=false;ShowInTaskbar=false;Topmost=true;ResizeMode=ResizeMode.NoResize;Focusable=false;
        var border=new Border{Background=new SolidColorBrush(Color.FromRgb(25,46,62)),CornerRadius=new CornerRadius(14),Padding=new Thickness(22,15,22,16),Margin=new Thickness(6),BorderBrush=new SolidColorBrush(Color.FromRgb(92,140,145)),BorderThickness=new Thickness(1)};
        var stack=new StackPanel();title=new TextBlock{Text="准备录音…",FontSize=15,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};preview=new TextBlock{FontSize=18,Foreground=new SolidColorBrush(Color.FromRgb(195,225,218)),TextWrapping=TextWrapping.Wrap,MaxHeight=120,Margin=new Thickness(0,7,0,0)};stack.Children.Add(title);stack.Children.Add(preview);border.Child=stack;Content=border;
        SourceInitialized+=(_,_)=>{var h=new WindowInteropHelper(this).Handle;SetWindowLongPtr(h,-20,new IntPtr(GetWindowLongPtr(h,-20).ToInt64()|0x08000000|0x80|0x20));};
        hide.Tick+=(_,_)=>{hide.Stop();Hide();};
    }
    public void Update(string status,string text="",bool dismiss=false)
    {
        title.Text=status;preview.Text=text;var area=SystemParameters.WorkArea;Left=area.Left+(area.Width-Width)/2;Top=area.Bottom-220;hide.Stop();if(!IsVisible)Show();if(dismiss)hide.Start();
    }
    public void Clear(){hide.Stop();Hide();}
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
}
