using Microsoft.UI.Xaml;

namespace ClassIsle;

public sealed partial class GuideWindow : Window
{
    private readonly Action _onFinished;

    public GuideWindow(Action onFinished)
    {
        _onFinished = onFinished;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 620));
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        _onFinished();
        Close();
    }
}
