using Microsoft.UI.Xaml;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Clario_Presenter;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    internal bool UsesPrivateThumbnail { get; }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // The shell can render a thumbnail as part of another window. Keep the
        // presenter (including the notes editor) iconic from its first frame.
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var enabled = 1;
        UsesPrivateThumbnail = DwmSetWindowAttribute(handle, 7, ref enabled, sizeof(int)) == 0 // FORCE_ICONIC_REPRESENTATION
            && DwmSetWindowAttribute(handle, 11, ref enabled, sizeof(int)) == 0; // DISALLOW_PEEK
        AppWindow.Resize(new SizeInt32(1440, 900));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public void MinimizeForPresentation()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Minimize();
    }

    public void SetSessionStatus(string label, Microsoft.UI.Xaml.Media.Brush brush)
    {
        TitleStatusText.Text = label;
        TitleStatusText.Foreground = brush;
        TitleStatusDot.Fill = brush;
    }
}
