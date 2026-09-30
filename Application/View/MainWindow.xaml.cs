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

        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainWindowVM();
            DataContext = vm;

            _ = vm.InitializeAsync();

            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.CanResize;
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowMessageHook);
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
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
                return;

            if (TryScrollHorizontally(e.Delta))
                e.Handled = true;
        }

        private bool TryScrollHorizontally(int delta)
        {
            if (!GetCursorPos(out var cursor))
                return false;

            var point = PointFromScreen(new Point(cursor.X, cursor.Y));
            var hit = InputHitTest(point) as DependencyObject;
            var viewer = FindScrollableParent(hit);
            if (viewer is null)
                return false;

            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - Math.Sign(delta) * 64);
            return true;
        }

        private static ScrollViewer? FindScrollableParent(DependencyObject? current)
        {
            while (current is not null)
            {
                if (current is ScrollViewer viewer && viewer.ScrollableWidth > 0)
                    return viewer;

                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }

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
