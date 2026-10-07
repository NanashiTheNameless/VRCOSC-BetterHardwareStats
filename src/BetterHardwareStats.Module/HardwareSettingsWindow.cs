using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VRCOSC.App.UI.Windows.Modules;
using VRCOSC.App.UI.Core;

namespace BetterHardwareStats.Module;
public sealed class HardwareSettingsWindow : Window, IManagedWindow
{
    private ICollectionView? _groups;
    private readonly BetterHardwareStatsModule _hardware;

    public HardwareSettingsWindow(BetterHardwareStatsModule module)
    {
        _hardware = module;
        // Compose the host window: its compiled XAML cannot be inherited across assemblies.
        var host = new ModuleSettingsWindow(module);
        var content = host.Content;
        host.Content = null;
        Content = content;
        Resources = host.Resources;
        DataContext = host;
        Title = host.Title;
        Width = host.Width;
        Height = host.Height;
        MinWidth = host.MinWidth;
        MinHeight = host.MinHeight;
        Closing += (_, _) => host.Close(); // retains the host's normal settings serialization
        Loaded += OnLoaded;
        Closed += (_, _) => _hardware.SettingsVisibilityChanged -= RefreshVisibility;
    }

    public object GetComparer() => _hardware;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        var list = Content is DependencyObject root ? FindGroups(root) : null;
        if (list?.ItemsSource is null) return;
        _groups = CollectionViewSource.GetDefaultView(list.ItemsSource);
        _groups.Filter = item => item is not SettingsGroupFormatted group || _hardware.IsGroupVisible(group.Title);
        _hardware.SettingsVisibilityChanged += RefreshVisibility;
        RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RefreshVisibility); return; }
        _groups?.Refresh();
    }

    private static ItemsControl? FindGroups(DependencyObject parent)
    {
        if (parent is ItemsControl items && items.ItemsSource is IEnumerable<object> source && source.FirstOrDefault() is SettingsGroupFormatted)
            return items;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindGroups(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
