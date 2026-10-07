using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VRCOSC.App.SDK.Modules.Attributes.Settings;
using SdkModule = VRCOSC.App.SDK.Modules.Module;

namespace BetterHardwareStats.Module;
public sealed class SamplingIntervalSettingView : UserControl
{
    public SamplingIntervalSettingView(SdkModule module, ModuleSetting setting)
    {
        var value = (SliderModuleSetting)setting;
        var editor = new Grid
        {
            Width = 130,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = setting.Description,
        };
        editor.ColumnDefinitions.Add(new ColumnDefinition());
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        var input = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 30 };
        input.SetBinding(TextBox.TextProperty, new Binding("Attribute.Value")
        {
            Source = value,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            Converter = new IntervalConverter(value),
        });
        void Commit()
        {
            var binding = input.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            binding?.UpdateTarget();
        }
        void Step(int direction)
        {
            Commit();
            value.Attribute.Value = Math.Clamp(value.Attribute.Value + direction * value.TickFrequency, value.MinValue, value.MaxValue);
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
        input.LostKeyboardFocus += (_, _) => Commit();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            if (e.Key is Key.Up or Key.Down) { Step(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        };
        editor.Children.Add(input);
        var buttons = new Grid();
        buttons.RowDefinitions.Add(new RowDefinition());
        buttons.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(buttons, 1);
        editor.Children.Add(buttons);
        for (var i = 0; i < 2; i++)
        {
            var direction = i == 0 ? 1 : -1;
            var button = new RepeatButton { Focusable = false, ToolTip = direction > 0 ? "Increase by 250 ms" : "Decrease by 250 ms" };
            var arrow = new Polygon
            {
                Points = direction > 0 ? new PointCollection { new(0, 5), new(5, 0), new(10, 5) }
                    : new PointCollection { new(0, 0), new(5, 5), new(10, 0) },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            arrow.SetBinding(Shape.FillProperty, new Binding(nameof(Foreground)) { Source = button });
            button.Content = arrow;
            button.Click += (_, _) => Step(direction);
            Grid.SetRow(button, i);
            buttons.Children.Add(button);
        }
        Content = editor;
    }

    private sealed class IntervalConverter(SliderModuleSetting setting) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            ((float)value).ToString("F0", culture);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            int.TryParse(value as string, NumberStyles.Integer, culture, out var number)
                ? (float)Math.Clamp(number, (int)setting.MinValue, (int)setting.MaxValue)
                : Binding.DoNothing;
    }
}
