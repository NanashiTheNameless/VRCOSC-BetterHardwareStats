using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VRCOSC.App.SDK.Modules.Attributes.Settings;
using SdkModule = VRCOSC.App.SDK.Modules.Module;

namespace BetterHardwareStats.Module;
public sealed class RefreshableDropdownSetting : StringModuleSetting
{
    public ObservableCollection<BetterHardwareStatsModule.SourceOption> Choices { get; } = [];
    public event Action? ChoicesChanged;
    public bool Refreshing { get; private set; }

    public RefreshableDropdownSetting(string title, string description,
        IEnumerable<BetterHardwareStatsModule.SourceOption> choices, string defaultValue)
        : base(title, description, typeof(RefreshableDropdownView), defaultValue)
    {
        ReplaceChoices(choices);
    }

    public void ReplaceChoices(IEnumerable<BetterHardwareStatsModule.SourceOption> choices)
    {
        var updated = choices.ToList();
        var saved = Attribute.Value;
        if (!updated.Any(c => c.Value == saved))
        {
            var previous = Choices.FirstOrDefault(c => c.Value == saved);
            updated.Add(new((previous?.Title.Replace(" (unavailable)", "") ?? saved) + " (unavailable)", saved));
        }
        Refreshing = true;
        try
        {
            Choices.Clear();
            foreach (var choice in updated) Choices.Add(choice);
            ChoicesChanged?.Invoke();
        }
        finally { Refreshing = false; }
    }
}

public sealed class RefreshableDropdownView : UserControl
{
    public RefreshableDropdownView(SdkModule module, ModuleSetting setting)
    {
        var dropdown = (RefreshableDropdownSetting)setting;
        var combo = new ComboBox
        {
            ItemsSource = dropdown.Choices,
            DisplayMemberPath = nameof(BetterHardwareStatsModule.SourceOption.Title),
            SelectedValuePath = nameof(BetterHardwareStatsModule.SourceOption.Value),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 130,
            ToolTip = setting.Description,
        };
        var synchronizing = false;
        void RestoreSelection()
        {
            synchronizing = true;
            try { combo.SelectedValue = dropdown.Attribute.Value; }
            finally { synchronizing = false; }
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (!synchronizing && !dropdown.Refreshing && combo.SelectedValue is string selected)
                dropdown.Attribute.Value = selected;
        };
        Loaded += (_, _) =>
        {
            dropdown.ChoicesChanged += RestoreSelection;
            // Keep the saved value when its device is unavailable.
            dropdown.ReplaceChoices(dropdown.Choices.ToArray());
            RestoreSelection();
        };
        Unloaded += (_, _) => dropdown.ChoicesChanged -= RestoreSelection;
        Content = combo;
    }
}
