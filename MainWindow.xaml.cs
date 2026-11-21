using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

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
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 640;

            int width = (int)Math.Clamp(_settings.WindowWidth, 960, 3840);
            int height = (int)Math.Clamp(_settings.WindowHeight, 640, 2160);
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
                _settings.WindowWidth = AppWindow.Size.Width;
                _settings.WindowHeight = AppWindow.Size.Height;
            }
            _settings.Save();
        }
    }

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
