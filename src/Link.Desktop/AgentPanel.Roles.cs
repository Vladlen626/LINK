using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Link.Desktop.Services;

namespace Link.Desktop;

// The card's kit: the specialists Link gives the task from its goal, the workspace's packs, and the skills read first.
public partial class AgentPanel
{
    /// <summary>The kit the card starts with: Link's pick from the goal, or a hand-over's own.</summary>
    private void ShowRoles(AgentCardModel card)
    {
        _roleIds = card.RoleIds;
        _skills = card.Skills ?? ProjectRoles.SkillsFor(GoalBox.Text, ProjectRoles.Of(_roleIds, card.Roles), card.WorkspaceSkills);
        ShowKit(card);
    }

    /// <summary>
    /// The kit as jellyfish with their names, pale: the agent has opened none yet. A hint names what each brings and a click
    /// opens its file; under them, the skills the agent is told to read first.
    /// </summary>
    private void ShowKit(AgentCardModel card)
    {
        RolesPanel.Children.Clear();
        var kit = ProjectRoles.Kit(_roleIds, [], card.Roles, card.WorkspaceSkills, ToolPacks.For(card.Workspace));
        foreach (var jellyfish in Jellyfish.Of(kit, (Brush)FindResource("MutedBrush"))) RolesPanel.Children.Add(KitChip(jellyfish));
        ReadFirstText.Text = "Read first: " + string.Join(", ", _skills);
        ReadFirstText.Visibility = _skills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A jellyfish of a turn's kit: its file, a pack's folder.</summary>
    private void Jellyfish_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Jellyfish { FilePath: { } path } }) OpenFileRequested?.Invoke(path);
    }

    private Button KitChip(Jellyfish jellyfish)
    {
        var chip = new Button
        {
            Style = (Style)FindResource("GhostButtonStyle"),
            Padding = new Thickness(6, 3, 8, 3),
            Margin = new Thickness(0, 6, 6, 0),
            ToolTip = jellyfish.Hint,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Control
                    {
                        Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0), Focusable = false, Foreground = jellyfish.Brush,
                        Opacity = jellyfish.Opacity, Template = (ControlTemplate)FindResource("LinkGlyphTemplate")
                    },
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Text = jellyfish.Name }
                }
            }
        };
        AutomationProperties.SetName(chip, jellyfish.Name);
        if (jellyfish.FilePath is { } path) chip.Click += (_, _) => OpenFileRequested?.Invoke(path);
        return chip;
    }
}
