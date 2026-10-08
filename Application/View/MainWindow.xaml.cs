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
        private HwndSource? _windowSource;
        private bool _deferredInitializationStarted;

        public MainWindow()
        {
            InitializeComponent();
            Behaviors.WindowWorkAreaBehavior.Attach(this);

            var vm = new MainWindowVM();
            DataContext = vm;

            ResizeMode = ResizeMode.CanResize;
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
            PreviewMouseWheel += OnPreviewMouseWheel;
            SizeChanged += (_, _) => vm.EnsureNavigationFits(ActualWidth);
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

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        // Event for functions that are triggerd by selecting a new TabItem
        private void NavigationBarSelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

    }
}
