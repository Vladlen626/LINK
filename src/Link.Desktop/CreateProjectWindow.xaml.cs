using System.Windows;
using System.Windows.Input;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class CreateProjectWindow : Window
{
    public CreateProjectWindow(string? initialName = null, bool isRename = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyleService.EnableBackdrop(this, ThemeService.IsDark);
        if (isRename)
        {
            Title = "Rename Project";
            HeadingText.Text = "Rename project";
            DescriptionText.Text = "Choose a concise name for this workspace.";
            ConfirmButton.Content = "Save name";
        }
        NameBox.Text = initialName ?? string.Empty;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string ProjectName => NameBox.Text.Trim();

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ProjectName)) DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void NameBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Create_OnClick(sender, e);
    }
}
