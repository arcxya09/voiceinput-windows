using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT;

namespace RealtimeTranscription.Desktop;

/// <summary>Native desktop Acrylic in a smooth capsule, on a transparent, non-activating HWND.</summary>
internal sealed class CapsuleSurface : IDisposable
{
    private readonly Window window;
    private readonly Grid host;
    private readonly Border capsule;
    private readonly Grid shadowHost = new() { IsHitTestVisible = false };
    private readonly SystemBackdropElement material = new() { Name = "OverlayMaterial", IsHitTestVisible = false };
    private readonly CapsuleAcrylicBackdrop acrylic;
    private readonly CapsuleTransparentBackdrop transparent = new();
    private readonly CapsuleWindowChrome chrome;
    private readonly UISettings settings = new();
    private SpriteVisual? shadowVisual;
    private DropShadow? shadow;
    private ShapeVisual? maskVisual;
    private CompositionRoundedRectangleGeometry? maskGeometry;
    private CompositionSpriteShape? maskShape;
    private CompositionColorBrush? maskFill;
    private CompositionVisualSurface? maskSurface;
    private CompositionSurfaceBrush? maskBrush;
    private bool disposed, dark, highContrast;
    private Color foreground;
    private bool effectsAllowed;
    private bool geometryQueued;

    internal bool AcrylicSupported => acrylic.AcrylicSupported;
    internal bool BorderModeConfigured => acrylic.BorderModeConfigured;
    internal bool HasAntialiasedAcrylic => acrylic.IsConnected && acrylic.BorderModeConfigured;
    internal bool IsUsingAcrylic => material.Visibility == Visibility.Visible && acrylic.MaterialState == SystemBackdropState.Active;
    internal string MaterialDiagnostics => $"Supported={AcrylicSupported}; SoftEdges={BorderModeConfigured}; Connected={acrylic.IsConnected}; State={acrylic.MaterialState?.ToString() ?? \"Solid\"}; EffectsAllowed={effectsAllowed}; Fallback={acrylic.FallbackReason}";

    public CapsuleSurface(Window window, Grid transparentHost, Border capsule)
    {
        this.window = window;
        host = transparentHost;
        this.capsule = capsule;
        host.DispatcherQueue.EnsureSystemDispatcherQueue();
        acrylic = new CapsuleAcrylicBackdrop(OnMaterialAvailabilityChanged);
        material.SystemBackdrop = acrylic;
        shadowHost.Margin = material.Margin = capsule.Margin;
        host.Background = new SolidColorBrush(Colors.Transparent);
        host.Children.Insert(0, shadowHost);
        host.Children.Insert(1, material);
        window.SystemBackdrop = transparent;
        chrome = new CapsuleWindowChrome(WinRT.Interop.WindowNative.GetWindowHandle(window));
        capsule.SizeChanged += CapsuleSizeChanged;
        capsule.Loaded += CapsuleLoaded;
        settings.AdvancedEffectsEnabledChanged += EffectsChanged;
        UpdateAppearance(capsule.ActualTheme == ElementTheme.Dark, false, Colors.Black);
    }

    public void UpdateAppearance(bool dark, bool highContrast, Color foreground)
    {
        if (disposed) return;
        this.dark = dark;
        this.highContrast = highContrast;
        this.foreground = foreground;
        effectsAllowed = !highContrast && settings.AdvancedEffectsEnabled;
        acrylic.UpdateAppearance(dark, highContrast);
        ApplyColors();
        UpdateGeometry();
    }

    private void EffectsChanged(UISettings sender, object args)
        => host.DispatcherQueue.TryEnqueue(() =>
        {
            if (!disposed) UpdateAppearance(dark, highContrast, foreground);
        });

    private void OnMaterialAvailabilityChanged() { if (!disposed) ApplyColors(); }

    private void ApplyColors()
    {
        Color fallback = highContrast ? settings.GetColorValue(UIColorType.Background)
            : dark ? Color.FromArgb(255, 31, 34, 40) : Color.FromArgb(255, 247, 248, 250);
        bool useMaterial = effectsAllowed && acrylic.IsConnected;
        // Keep the element loaded while establishing its composition target.
        material.Visibility = effectsAllowed ? Visibility.Visible : Visibility.Collapsed;
        capsule.Background = new SolidColorBrush(useMaterial ? Colors.Transparent : fallback);
        capsule.BorderThickness = new Thickness(highContrast ? 1.5 : 1);
        capsule.BorderBrush = new SolidColorBrush(highContrast ? foreground
            : dark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(42, 34, 44, 57));
        shadowHost.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        if (shadow != null) shadow.Opacity = dark ? .23f : .15f;
    }

    private void CapsuleLoaded(object sender, RoutedEventArgs args) => UpdateGeometry();
    private void CapsuleSizeChanged(object sender, SizeChangedEventArgs args) => UpdateGeometry();

    private void UpdateGeometry()
    {
        if (disposed || !capsule.IsLoaded || capsule.ActualWidth <= 0 || capsule.ActualHeight <= 0) return;
        var size = new Vector2((float)capsule.ActualWidth, (float)capsule.ActualHeight);
        double radius = Math.Min(capsule.ActualWidth, capsule.ActualHeight) / 2;
        capsule.CornerRadius = material.CornerRadius = new CornerRadius(radius);
        material.Margin = shadowHost.Margin = capsule.Margin;
        if (shadowVisual == null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            maskGeometry = compositor.CreateRoundedRectangleGeometry();
            maskFill = compositor.CreateColorBrush(Colors.White);
            maskShape = compositor.CreateSpriteShape(maskGeometry);
            maskShape.FillBrush = maskFill;
            maskVisual = compositor.CreateShapeVisual();
            maskVisual.Shapes.Add(maskShape);
            maskSurface = compositor.CreateVisualSurface();
            maskSurface.SourceVisual = maskVisual;
            maskBrush = compositor.CreateSurfaceBrush(maskSurface);
            shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = 12;
            shadow.Offset = new Vector3(0, 3, 0);
            shadow.Color = Colors.Black;
            shadow.Opacity = dark ? .23f : .15f;
            shadow.Mask = maskBrush;
            shadowVisual = compositor.CreateSpriteVisual();
            shadowVisual.Shadow = shadow;
            shadowVisual.BorderMode = CompositionBorderMode.Soft;
            ElementCompositionPreview.SetElementChildVisual(shadowHost, shadowVisual);
        }
        maskGeometry!.Size = size;
        maskGeometry.CornerRadius = new Vector2((float)radius);
        maskVisual!.Size = size;
        maskSurface!.SourceSize = size;
        shadowVisual.Size = size;

        // The external backdrop has its own antialiasing switch. Set the local
        // placement visual as well, after SystemBackdropElement has arranged it.
        if (!geometryQueued)
        {
            geometryQueued = true;
            host.DispatcherQueue.TryEnqueue(() =>
            {
                geometryQueued = false;
                if (!disposed && ElementCompositionPreview.GetElementChildVisual(material) is { } placement)
                    placement.BorderMode = CompositionBorderMode.Soft;
            });
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        settings.AdvancedEffectsEnabledChanged -= EffectsChanged;
        capsule.Loaded -= CapsuleLoaded;
        capsule.SizeChanged -= CapsuleSizeChanged;
        material.SystemBackdrop = null;
        acrylic.Dispose();
        window.SystemBackdrop = null;
        transparent.Dispose();
        ElementCompositionPreview.SetElementChildVisual(shadowHost, null);
        shadowVisual?.Dispose();
        shadow?.Dispose();
        maskBrush?.Dispose();
        maskSurface?.Dispose();
        maskVisual?.Dispose();
        maskShape?.Dispose();
        maskFill?.Dispose();
        maskGeometry?.Dispose();
        chrome.Dispose();
    }
}

/// <summary>Keeps desktop Acrylic active without activating the user's input window.</summary>
internal sealed partial class CapsuleAcrylicBackdrop(Action availabilityChanged) : SystemBackdrop, IDisposable
{
    private DesktopAcrylicController? controller;
    private SystemBackdropConfiguration? configuration;
    private ICompositionSupportsSystemBackdrop? target;
    private bool dark, highContrast, disposed;
    internal bool IsConnected => controller != null;
    internal bool AcrylicSupported { get; private set; }
    internal bool BorderModeConfigured { get; private set; }
    internal SystemBackdropState? MaterialState => controller?.State;
    internal string FallbackReason { get; private set; } = "NotConnected";

    public void UpdateAppearance(bool dark, bool highContrast)
    {
        this.dark = dark;
        this.highContrast = highContrast;
        if (configuration == null) return;
        configuration.IsInputActive = true;
        configuration.IsHighContrast = highContrast;
        configuration.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (disposed) return;
        AcrylicSupported = DesktopAcrylicController.IsSupported();
        if (!AcrylicSupported) { FallbackReason = "Unsupported"; return; }
        // WinUI issue #11086: the external link defaults to hard edges. Its
        // documented ExternalBackdropBorderMode API is still absent from the
        // stable C# projection. Query the interface before calling that single
        // property; an unsupported runtime gets a smooth solid XAML capsule.
        BorderModeConfigured = ExternalBackdropAntialiasing.TryEnable(connectedTarget);
        if (!BorderModeConfigured) { FallbackReason = "SoftBorderUnavailable"; return; }
        target = connectedTarget;
        configuration = new SystemBackdropConfiguration();
        UpdateAppearance(dark, highContrast);
        try
        {
            controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
            controller.SetSystemBackdropConfiguration(configuration);
            if (!controller.AddSystemBackdropTarget(connectedTarget))
            {
                controller.Dispose();
                controller = null;
                FallbackReason = "TargetRejected";
            }
            else FallbackReason = "None";
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
            controller?.Dispose();
            controller = null;
            FallbackReason = $"ControllerError:{ex.HResult:X8}";
        }
        availabilityChanged();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ReleaseTarget();
        base.OnTargetDisconnected(disconnectedTarget);
        availabilityChanged();
    }

    private void ReleaseTarget()
    {
        if (controller != null)
        {
            if (target != null) controller.RemoveSystemBackdropTarget(target);
            controller.Dispose();
            controller = null;
        }
        target = null;
        configuration = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseTarget();
    }
}

internal static class ExternalBackdropAntialiasing
{
    // IContentExternalBackdropLink: IInspectable, DispatcherQueue getter,
    // ExternalBackdropBorderMode getter/setter, PlacementVisual getter.
    private static readonly Guid InterfaceId = new("1054BF83-B35B-5FDE-8DD7-AC3BB3E6CE27");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBorderMode(IntPtr instance, int mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBorderMode(IntPtr instance, out int mode);

    public static bool TryEnable(ICompositionSupportsSystemBackdrop target)
    {
        IntPtr unknown = IntPtr.Zero, link = IntPtr.Zero;
        try
        {
            unknown = MarshalInspectable<ICompositionSupportsSystemBackdrop>.FromManaged(target);
            var iid = InterfaceId;
            if (Marshal.QueryInterface(unknown, ref iid, out link) < 0 || link == IntPtr.Zero) return false;
            var vtable = Marshal.ReadIntPtr(link);
            var setter = Marshal.GetDelegateForFunctionPointer<SetBorderMode>(Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size));
            if (setter(link, (int)CompositionBorderMode.Soft) < 0) return false;
            var getter = Marshal.GetDelegateForFunctionPointer<GetBorderMode>(Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size));
            return getter(link, out int actual) >= 0 && actual == (int)CompositionBorderMode.Soft;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return false; }
        finally
        {
            if (link != IntPtr.Zero) Marshal.Release(link);
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
            GC.KeepAlive(target);
        }
    }
}

/// <summary>Transparent desktop base; the capsule alone supplies the visible material.</summary>
internal sealed partial class CapsuleTransparentBackdrop : SystemBackdrop, IDisposable
{
    private Windows.UI.Composition.Compositor? compositor;
    private Windows.UI.Composition.CompositionColorBrush? brush;
    private ICompositionSupportsSystemBackdrop? target;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        compositor = new Windows.UI.Composition.Compositor();
        brush = compositor.CreateColorBrush(Colors.Transparent);
        target = connectedTarget;
        connectedTarget.SystemBackdrop = brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ReleaseTarget();
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private void ReleaseTarget()
    {
        if (target != null) target.SystemBackdrop = null;
        target = null;
        brush?.Dispose(); brush = null;
        compositor?.Dispose(); compositor = null;
    }

    public void Dispose() => ReleaseTarget();
}

/// <summary>Borderless alpha-composed chrome used only by the recognition capsule.</summary>
internal sealed class CapsuleWindowChrome : IDisposable
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const nuint SubclassId = 0x56494350;
    private readonly IntPtr hwnd;
    private readonly SubclassProc callback;
    private bool disposed;

    public CapsuleWindowChrome(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        callback = WindowProc;
        if (!SetWindowSubclass(hwnd, callback, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置识别胶囊窗口。");
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(NormalizeStyle(GetWindowLongPtr(hwnd, GwlStyle).ToInt64())));
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(NormalizeExtendedStyle(GetWindowLongPtr(hwnd, GwlExStyle).ToInt64())));
        if (!SetLayeredWindowAttributes(hwnd, 0, 255, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置识别胶囊鼠标穿透。");
        ConfigureDwm();
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
    }

    private static long NormalizeStyle(long value) => (value & ~0x00CF0000L) | 0x80000000L;
    private static long NormalizeExtendedStyle(long value)
        => (value & ~(0x00000100L | 0x00000200L | 0x00000001L | 0x00020000L | 0x00040000L))
            | 0x08000000L | 0x00080000L | 0x00000080L | 0x00000020L;

    private void ConfigureDwm()
    {
        if (disposed) return;
        Attribute(2, 1); // DWMNCRP_DISABLED: XAML owns the capsule outline.
        Attribute(3, 1); // The host performs one coordinated fade.
        Attribute(33, 1); // DWMWCP_DONOTROUND; do not clip the transparent shadow gutter.
        Attribute(34, -2); // DWMWA_COLOR_NONE.
        var margins = new Margins();
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region != IntPtr.Zero)
        {
            try
            {
                var blur = new BlurBehind { Flags = 3, Enable = true, Region = region };
                _ = DwmEnableBlurBehindWindow(hwnd, ref blur);
            }
            finally { DeleteObject(region); }
        }
    }

    private void Attribute(uint attribute, int value)
        => _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x0083) return IntPtr.Zero; // WM_NCCALCSIZE: no frame inset.
        if (message == 0x0084) return new IntPtr(-1); // HTTRANSPARENT.
        if (message == 0x0021) return new IntPtr(3); // MA_NOACTIVATE.
        if (message == 0x0014 && GetClientRect(window, out var client))
        {
            FillRect(wParam, ref client, GetStockObject(4)); // BLACK_BRUSH: zero-alpha base.
            return new IntPtr(1);
        }
        if (message == 0x007C && lParam != IntPtr.Zero)
        {
            int index = unchecked((int)wParam.ToInt64());
            if (index is GwlStyle or GwlExStyle)
            {
                var styles = Marshal.PtrToStructure<StyleStruct>(lParam);
                styles.NewStyle = unchecked((uint)(index == GwlStyle
                    ? NormalizeStyle(styles.NewStyle) : NormalizeExtendedStyle(styles.NewStyle)));
                Marshal.StructureToPtr(styles, lParam, false);
                return IntPtr.Zero;
            }
        }
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0082) Dispose();
        else if (message is 0x031A or 0x031E) ConfigureDwm();
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RemoveWindowSubclass(hwnd, callback, SubclassId);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public uint OldStyle, NewStyle; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
