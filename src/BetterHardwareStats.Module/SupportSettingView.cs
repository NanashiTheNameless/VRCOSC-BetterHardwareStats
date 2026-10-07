using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VRCOSC.App.SDK.Modules.Attributes.Settings;
using SdkModule = VRCOSC.App.SDK.Modules.Module;

namespace BetterHardwareStats.Module;
public sealed class SupportSettingView : UserControl
{
    public SupportSettingView(SdkModule module, ModuleSetting setting)
    {
        var links = new WrapPanel
        {
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        (string Name, string Url)[] destinations =
        [
            ("GitHub Sponsors", "https://github.com/sponsors/NanashiTheNameless"),
            ("Buy Me a Coffee", "https://buymeacoffee.com/NamelessNanashi"),
            ("Ko-fi", "https://ko-fi.com/NanashiTheNameless"),
            ("Liberapay", "https://liberapay.com/NamelessNanashi"),
            ("Throne", "https://throne.com/NamelessNanashi"),
        ];
        foreach (var (name, url) in destinations)
        {
            var button = new Button
            {
                Content = name,
                ToolTip = url,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.Normal,
                MinHeight = 32,
            };
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception error) { module.Log($"Could not open support link: {error.Message}"); }
            };
            links.Children.Add(button);
        }
        Content = links;
    }
}
