using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DiskGo;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        bool isAdmin = ShellHelpers.IsAdministrator();
        AppTitleBar.Subtitle = isAdmin ? "管理员" : "标准用户";
        TitleElevateItem.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

        ConfigurePresenter();
        Closed += WindowClosed;

        RootFrame.Navigate(typeof(MainPage));
    }

    private void ConfigurePresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Settings are device-independent pixels. AppWindow speaks physical pixels.
            double scale = DisplayScale();
            presenter.PreferredMinimumWidth = ToScreen(960, scale);
            presenter.PreferredMinimumHeight = ToScreen(640, scale);

            int width = ToScreen(Math.Clamp(_settings.WindowWidth, 960, 3840), scale);
            int height = ToScreen(Math.Clamp(_settings.WindowHeight, 640, 2160), scale);
            AppWindow.Resize(new SizeInt32(width, height));

            if (_settings.WindowMaximized)
                presenter.Maximize();
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            bool maximized = presenter.State == OverlappedPresenterState.Maximized;
            _settings.WindowMaximized = maximized;
            if (!maximized)
            {
                double scale = DisplayScale();
                _settings.WindowWidth = AppWindow.Size.Width / scale;
                _settings.WindowHeight = AppWindow.Size.Height / scale;
            }
            _settings.Save();
        }
    }

    private double DisplayScale()
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        return dpi == 0 ? 1 : dpi / 96d;
    }

    private static int ToScreen(double dips, double scale) => Math.Max(1, (int)Math.Round(dips * scale));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private MainPage? Page => RootFrame.Content as MainPage;

    private void TitleExportClicked(object sender, RoutedEventArgs e) => Page?.OpenExportDialog();
    private void TitleSettingsClicked(object sender, RoutedEventArgs e) => Page?.OpenSettingsDialog();
    private void TitleDumpMftClicked(object sender, RoutedEventArgs e) => Page?.DumpMftFromMenu();
    private void TitleNewWindowClicked(object sender, RoutedEventArgs e) => ShellHelpers.OpenNewWindow();

    private void TitleElevateClicked(object sender, RoutedEventArgs e)
    {
        if (ShellHelpers.RestartAsAdministrator())
            Close();
    }
}
