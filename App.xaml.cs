using Microsoft.UI.Xaml;

namespace Clario_Presenter;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    // Inno Setup checks this named object before replacing files in use.
    private readonly Mutex _running = new(false, @"Local\ClarioPresenter.Running.5EC3B50D");

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
