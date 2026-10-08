using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Constrains each window to its current monitor's work area, in WPF units.</summary>
public static class WindowWorkAreaBehavior
{
    private static readonly ConditionalWeakTable<Window, State> States = new();
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach((Window)sender)));
    }

    public static void Attach(Window window)
    {
        if (States.TryGetValue(window, out _)) return;
        var state = new State(window);
        States.Add(window, state);
        state.Attach();
    }

    private sealed class State(Window window)
    {
        private readonly double _minimumWidth = window.MinWidth;
        private readonly double _minimumHeight = window.MinHeight;
        private HwndSource? _source;

        public void Attach()
        {
            window.SourceInitialized += OnSourceInitialized;
            window.LocationChanged += OnLocationChanged;
            window.Closed += OnClosed;
            OnSourceInitialized(null, EventArgs.Empty);
        }

        private void OnSourceInitialized(object? sender, EventArgs args)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || _source is not null) return;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(OnMessage);
            UpdateBounds();
        }

        private void OnLocationChanged(object? sender, EventArgs args) => UpdateBounds();

        private void OnClosed(object? sender, EventArgs args)
        {
            _source?.RemoveHook(OnMessage);
            window.SourceInitialized -= OnSourceInitialized;
            window.LocationChanged -= OnLocationChanged;
            window.Closed -= OnClosed;
            States.Remove(window);
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0024 && TryGetMonitor(hwnd, out var monitor)) // WM_GETMINMAXINFO
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition = new NativePoint(monitor.Work.Left - monitor.Monitor.Left, monitor.Work.Top - monitor.Monitor.Top);
                limits.MaxSize = new NativePoint(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
                limits.MaxTrackSize = limits.MaxSize;
                limits.MinTrackSize.X = Math.Min(limits.MinTrackSize.X, limits.MaxSize.X);
                limits.MinTrackSize.Y = Math.Min(limits.MinTrackSize.Y, limits.MaxSize.Y);
                Marshal.StructureToPtr(limits, lParam, false);
                handled = true;
            }
            else if (message is 0x02E0 or 0x007E or 0x001A) // DPI / display / work-area change
            {
                // WPF must adopt the new DPI before device pixels are converted.
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateBounds));
            }
            return IntPtr.Zero;
        }

        private void UpdateBounds()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !TryGetMonitor(handle, out var monitor)) return;
            var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var size = transform.Transform(new Vector(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top));
            var width = Math.Max(1, size.X); var height = Math.Max(1, size.Y);
            // A minimum larger than the monitor must not defeat the maximum.
            window.MinWidth = Math.Min(_minimumWidth, width);
            window.MinHeight = Math.Min(_minimumHeight, height);
            window.MaxWidth = width; window.MaxHeight = height;
            if (window.WindowState != WindowState.Normal) return;
            if (window.Width > width) window.Width = width;
            if (window.Height > height) window.Height = height;
        }
    }

    private static bool TryGetMonitor(IntPtr hwnd, out MonitorInfo monitor)
    {
        monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var handle = MonitorFromWindow(hwnd, 2);
        return handle != IntPtr.Zero && GetMonitorInfo(handle, ref monitor);
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
}
