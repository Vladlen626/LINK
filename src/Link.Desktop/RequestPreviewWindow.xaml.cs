using System.Windows;

namespace Link.Desktop;

public partial class RequestPreviewWindow : Window
{
    public RequestPreviewWindow(string json)
    {
        InitializeComponent();
        JsonBox.Text = json;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
