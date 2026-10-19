using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatterBabel.App.ViewModels;
using BatterBabel.Core.Hosts;
using BatterBabel.Core.Simulation;

namespace BatterBabel.App;

/// <summary>
/// 主窗口装配点：把 Core 的两个实现注入 ViewModel（构造器注入，ViewModel 不知道具体类型）。
/// 原型阶段注入的是模拟探测器 + 拒绝写入的 hosts 实现，见 BB-WPF-PROTOTYPE.md 的边界说明。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var model = new MainViewModel(new SimulatedRouteOptimizer(), new RefusingHostsApplier());
        DataContext = model;

        // 日志追加后滚到最新一条（ItemsControl 没有 ScrollIntoView，滚动的是外层 ScrollViewer）。
        model.Logs.CollectionChanged += OnLogsChanged;

        var args = Environment.GetCommandLineArgs();
        var demo = args.Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = ValueAfter(args, "--screenshot");

        // --demo：启动后自动跑一次模拟优选，用于截图/演示（不改变任何默认行为）。
        if (demo)
        {
            Loaded += async (_, _) =>
            {
                await Task.Delay(250);
                if (model.StartCommand.CanExecute(null))
                {
                    model.StartCommand.Execute(null);
                }
            };
        }

        // --screenshot <path>：等界面稳定后把真实渲染结果写成 PNG，然后退出。
        // 用 RenderTargetBitmap 而不是屏幕抓取：不依赖窗口是否在最前、不继承调用者管道、可用于 CI。
        if (screenshotPath is not null)
        {
            Loaded += async (_, _) =>
            {
                await Task.Delay(demo ? 2600 : 900);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Capture(screenshotPath);
                Application.Current.Shutdown();
            };
        }
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private void Capture(string path)
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(full);
        encoder.Save(stream);
        Console.WriteLine($"screenshot {full} {width}x{height} {new FileInfo(full).Length} bytes");
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => LogScroll.ScrollToEnd()), DispatcherPriority.Background);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 深色标题栏：Windows 11 用属性 20，旧版回退 19；不支持时静默忽略（不影响功能）。
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
