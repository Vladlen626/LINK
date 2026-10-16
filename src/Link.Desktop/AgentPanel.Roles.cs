using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Link.Desktop.Services;

namespace Link.Desktop;

// The card's specialist chips and the skills that go with the goal and the specialist.
public partial class AgentPanel
{
    // General, no role, is null.
    private string? SelectedRoleId => _roleId;
    private AgentRole? SelectedRole => _card?.Roles.FirstOrDefault(role => role.Id == _roleId);

    /// <summary>The specialist and the skills the card starts with: Link's pick from the goal, or a hand-over's own.</summary>
    private void ShowRoles(AgentCardModel card)
    {
        _roleChosen = card.FixedProvider;
        _pickChangedByRun = false;
        _skillsOff.Clear();
        _skillsOn.Clear();
        RoleRow.Visibility = card.Roles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowRoleChips(card);
        SelectRole(card.RoleId);
        SkillsRow.Visibility = card.WorkspaceSkills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSkills(card.Skills ?? PickedSkills());
    }

    /// <summary>One chip for General and one for each specialist, each with its jellyfish in the specialist's colour.</summary>
    private void ShowRoleChips(AgentCardModel card)
    {
        RolesPanel.Children.Clear();
        _roleChips.Clear();
        if (card.Roles.Count == 0) return;
        AddRoleChip(null, "General", RoleBrushes.Link, "No specialist: the agent works as its rules say");
        foreach (var role in card.Roles) AddRoleChip(role.Id, role.Name, RoleBrushes.Of(ProjectRoles.ColorOf(role, card.Roles)), role.Description);
    }

    private void AddRoleChip(string? id, string name, Brush color, string tip)
    {
        var look = new Border
        {
            Height = 28,
            Padding = new Thickness(9, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Control { Width = 13, Height = 13, Margin = new Thickness(0, 0, 7, 0), Focusable = false, Foreground = color, Template = (ControlTemplate)FindResource("LinkGlyphTemplate") },
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Text = name }
                }
            }
        };
        var button = new Button
        {
            Style = (Style)FindResource("GhostButtonStyle"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 6, 6, 0),
            ToolTip = tip,
            Content = look
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => PickRole(id);
        RolesPanel.Children.Add(button);
        _roleChips.Add(new RoleChip(id, look, color));
    }

    /// <summary>The user's pick of a specialist: it is theirs from now on, and the skills follow it.</summary>
    private void PickRole(string? id)
    {
        _roleChosen = true;
        SelectRole(id);
        ShowSkills(PickedSkills());
    }

    private void SelectRole(string? id)
    {
        _roleId = _card?.Roles.Any(role => role.Id == id) == true ? id : null;
        // The picked chip is filled and rimmed in its specialist's colour; the others are quiet. They change in place.
        foreach (var chip in _roleChips)
        {
            var picked = chip.Id == _roleId;
            chip.Look.BorderBrush = picked ? chip.Color : (Brush)FindResource("GlassBorderSoftBrush");
            chip.Look.Background = picked ? (Brush)FindResource("ActiveContextBrush") : Brushes.Transparent;
        }

        RolePickText.Visibility = _roleId is not null && !_roleChosen ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record RoleChip(string? Id, Border Look, Brush Color);

    /// <summary>The skills for the goal and the specialist as they stand: Link's picks without those taken off, and those added.</summary>
    private IReadOnlyList<string> PickedSkills() =>
        _card is null
            ? []
            : [.. ProjectRoles.SkillsFor(GoalBox.Text, SelectedRole, _card.WorkspaceSkills).Where(name => !_skillsOff.Contains(name)).Union(_skillsOn, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The skills as chips: a click takes one off, and the last chip adds one of the workspace's that is not there.</summary>
    private void ShowSkills(IReadOnlyList<string> skills)
    {
        _skills = skills;
        SkillsPanel.Children.Clear();
        foreach (var name in skills)
        {
            var chip = new Button { Style = (Style)FindResource("SmallChipStyle"), Content = name + " ✕", ToolTip = "Take it off: the agent is not told to read it first" };
            chip.Click += (_, _) =>
            {
                _skillsOn.Remove(name);
                _skillsOff.Add(name);
                ShowSkills([.. _skills.Where(each => each != name)]);
            };
            SkillsPanel.Children.Add(chip);
        }

        var others = _card?.WorkspaceSkills.Where(skill => !skills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase)).ToArray() ?? [];
        if (others.Length == 0) return;
        var add = new Button { Style = (Style)FindResource("SmallChipStyle"), Content = "+ skill", ToolTip = "Add a skill of the workspace: the agent is told to read it first" };
        add.Click += (_, _) => ShowMenu(add, [[.. others.Select(skill => new PanelAction(skill.Name, skill.Description, () =>
        {
            _skillsOff.Remove(skill.Name);
            _skillsOn.Add(skill.Name);
            ShowSkills([.. _skills, skill.Name]);
        }))]], PlacementMode.Bottom);
        SkillsPanel.Children.Add(add);
    }
}
