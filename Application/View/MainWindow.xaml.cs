using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int WmMouseHorizontalWheel = 0x020E;
        private const int WmGetMinMaxInfo = 0x0024;
        private const uint MonitorDefaultToNearest = 0x00000002;
        private HwndSource? _windowSource;
        private bool _deferredInitializationStarted;

        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainWindowVM();
            DataContext = vm;

            ResizeMode = ResizeMode.CanResize;
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
            PreviewMouseWheel += OnPreviewMouseWheel;
            SizeChanged += (_, _) => vm.EnsureNavigationFits(ActualWidth);
            LocationChanged += (_, _) => UpdateMaximumWindowSize();
            ContentRendered += async (_, _) =>
            {
                if (_deferredInitializationStarted)
                    return;
                _deferredInitializationStarted = true;
                vm.EnsureNavigationFits(ActualWidth);
                var startupElapsed = App.StartupElapsed;
                ApplicationLogService.Instance.Information(
                    "Anwendungsstart",
                    $"Hauptfenster nach {startupElapsed.TotalMilliseconds:F0} ms dargestellt; nachgelagerte Rollen- und Rechnerinitialisierung startet jetzt.");
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await vm.InitializeAsync();
            };
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowMessageHook);
            UpdateMaximumWindowSize();
            // Maximize only after the native work-area hook is active. Custom
            // chrome otherwise uses the virtual desktop and can extend beyond
            // the current monitor or underneath its taskbar.
            WindowState = WindowState.Maximized;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmGetMinMaxInfo)
            {
                ConstrainMaximizedWindowToWorkArea(hwnd, lParam);
                handled = true;
                return IntPtr.Zero;
            }

            if (message != WmMouseHorizontalWheel)
                return IntPtr.Zero;

            var delta = unchecked((short)((long)wParam >> 16));
            if (TryScrollHorizontally(delta))
                handled = true;

            return IntPtr.Zero;
        }

        private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var shiftPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (shiftPressed)
            {
                if (TryScrollHorizontally(e.Delta))
                    e.Handled = true;
                return;
            }

            var hit = e.OriginalSource as DependencyObject;
            if (FindAncestor<ComboBox>(hit) is not null)
                return;

            var viewer = FindScrollableParent(hit, horizontal: false);
            if (viewer is null)
                return;

            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - ScaleWheelDelta(e.Delta));
            e.Handled = true;
        }

        private static double ScaleWheelDelta(int delta) => delta / 120d * 32d;

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current is not null)
            {
                if (current is T match)
                    return match;
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }

        private bool TryScrollHorizontally(int delta)
        {
            if (!GetCursorPos(out var cursor))
                return false;

            var point = PointFromScreen(new Point(cursor.X, cursor.Y));
            var hit = InputHitTest(point) as DependencyObject;
            var viewer = FindScrollableParent(hit, horizontal: true);
            if (viewer is null)
                return false;

            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - ScaleWheelDelta(delta));
            return true;
        }

        private static ScrollViewer? FindScrollableParent(DependencyObject? current, bool horizontal)
        {
            while (current is not null)
            {
                if (current is ScrollViewer viewer &&
                    (horizontal ? viewer.ScrollableWidth > 0 : viewer.ScrollableHeight > 0))
                    return viewer;

                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }

        /*
         * Keep mouse-wheel navigation independent from item selection. WPF's
         * default logical scrolling advances complete rows; pixel scrolling
         * above prevents container details from disappearing between steps.
         */

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

        private static void ConstrainMaximizedWindowToWorkArea(IntPtr windowHandle, IntPtr minMaxInfoPointer)
        {
            var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            if (monitorHandle == IntPtr.Zero)
                return;

            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
                return;

            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
            var workArea = monitorInfo.WorkArea;
            var monitorArea = monitorInfo.MonitorArea;
            minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
            minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
            minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
            minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
            minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
            Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
        }

        private void UpdateMaximumWindowSize()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == IntPtr.Zero)
                return;
            var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitorHandle == IntPtr.Zero || !GetMonitorInfo(monitorHandle, ref monitorInfo))
                return;

            var source = PresentationSource.FromVisual(this);
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = fromDevice.Transform(new Point(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top));
            var bottomRight = fromDevice.Transform(new Point(
                monitorInfo.WorkArea.Right,
                monitorInfo.WorkArea.Bottom));
            MaxWidth = Math.Max(MinWidth, bottomRight.X - topLeft.X);
            MaxHeight = Math.Max(MinHeight, bottomRight.Y - topLeft.Y);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect MonitorArea;
            public NativeRect WorkArea;
            public uint Flags;
        }

        // Event for functions that are triggerd by selecting a new TabItem
        private void NavigationBarSelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

    }
}
