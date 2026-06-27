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
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D2D")),
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
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(header, 0);

            _textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#121212")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D2D")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                SelectionBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };
            _textBox.KeyDown += TextBox_KeyDown;
            Grid.SetRow(_textBox, 1);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(buttons, 2);

            var cancel = MakeButton("Cancel", "#00000000", "#E0E0E0", "#2D2D2D", "#2D2D30");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            var ok = MakeButton("OK", "#007ACC", "#FFFFFF", "#007ACC", "#0098FF");
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

        private static Button MakeButton(string content, string bg, string fg, string borderBrush, string hoverBg)
        {
            var button = new Button
            {
                Content = content,
                Width = 75,
                Height = 26,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg)),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderBrush)),
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
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverBg))));
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
