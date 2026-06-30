using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Filey
{
    /// <summary>
    /// A minimal, dark-themed free-text input dialog built entirely in code so it
    /// needs no XAML/csproj registration. Returns the entered text via <see cref="Value"/>.
    /// </summary>
    public class InputDialog : Window
    {
        private readonly TextBox _textBox;

        public string Value { get; private set; }

        public InputDialog(string title, string prompt, string initialValue)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            var border = new Border
            {
                Background = Palette("AppPanelBackgroundBrush", "#1E1E1E"),
                BorderBrush = Palette("AppBorderBrush", "#2D2D2D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16)
            };
            border.MouseLeftButtonDown += (s, e) => DragMove();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = prompt,
                Foreground = Palette("AppTextPrimaryBrush", "#FFFFFF"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            header.SetResourceReference(TextBlock.FontSizeProperty, "TitleFontSize");
            Grid.SetRow(header, 0);
 
            _textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Background = Palette("AppPanelDeepBrush", "#121212"),
                Foreground = Palette("AppTextPrimaryBrush", "#E0E0E0"),
                BorderBrush = Palette("AppBorderBrush", "#2D2D2D"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                SelectionBrush = Palette("AppAccentBrush", "#007ACC"),
                FontFamily = Application.Current?.TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI")
            };
            _textBox.SetResourceReference(TextBox.FontSizeProperty, "NormalFontSize");
            _textBox.KeyDown += TextBox_KeyDown;
            Grid.SetRow(_textBox, 1);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(buttons, 2);

            var cancel = MakeButton("Cancel",
                Brushes.Transparent,
                Palette("AppTextPrimaryBrush", "#E0E0E0"),
                Palette("AppBorderBrush", "#2D2D2D"),
                Palette("AppHoverBrush", "#2D2D30"));
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            var ok = MakeButton("OK",
                Palette("AppAccentBrush", "#007ACC"),
                Palette("AppTextOnAccentBrush", "#FFFFFF"),
                Palette("AppAccentBrush", "#007ACC"),
                Palette("AppAccentHoverBrush", "#0098FF"));
            ok.Click += (s, e) => Submit();

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            grid.Children.Add(header);
            grid.Children.Add(_textBox);
            grid.Children.Add(buttons);
            border.Child = grid;
            Content = border;

            Loaded += (s, e) => { _textBox.Focus(); _textBox.SelectAll(); };
        }

        /// <summary>Resolves a brush from the active theme palette, falling back to a literal hex
        /// color if the resource is unavailable.</summary>
        private static Brush Palette(string key, string fallbackHex)
        {
            return Application.Current?.TryFindResource(key) as Brush
                ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
        }

        private static Button MakeButton(string content, Brush bg, Brush fg, Brush borderBrush, Brush hoverBg)
        {
            var button = new Button
            {
                Content = content,
                Width = 75,
                Height = 26,
                Foreground = fg,
                Background = bg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1)
            };

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new System.Windows.FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetValue(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var contentFactory = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, hoverBg));
            template.Triggers.Add(hover);

            button.Template = template;
            return button;
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Submit();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void Submit()
        {
            string text = _textBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show("Value cannot be empty.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Value = text;
            DialogResult = true;
            Close();
        }
    }
}
