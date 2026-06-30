using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Filey
{
    public partial class AddressBar : UserControl
    {
        public static readonly DependencyProperty CurrentPathProperty =
            DependencyProperty.Register(
                nameof(CurrentPath),
                typeof(string),
                typeof(AddressBar),
                new PropertyMetadata(string.Empty, OnCurrentPathChanged));

        public static readonly DependencyProperty CanGoBackProperty =
            DependencyProperty.Register(
                nameof(CanGoBack),
                typeof(bool),
                typeof(AddressBar),
                new PropertyMetadata(false));

        public static readonly DependencyProperty CanGoForwardProperty =
            DependencyProperty.Register(
                nameof(CanGoForward),
                typeof(bool),
                typeof(AddressBar),
                new PropertyMetadata(false));

        public event EventHandler<string> NavigationRequested;
        public event EventHandler GoBackRequested;
        public event EventHandler GoForwardRequested;

        /// <summary>Raised on left-click of the Home button: navigate this pane to its Home.</summary>
        public event EventHandler HomeRequested;

        /// <summary>Raised on right-click of the Home button: set this pane's Home to its current path.</summary>
        public event EventHandler SetHomeRequested;

        /// <summary>Raised when the user activates a result from the inline index search.</summary>
        public event EventHandler<FolderItem> SearchResultChosen;

        private bool _isEditMode;
        private bool _isSearchMode;
        private readonly DispatcherTimer _searchDebounce;

        public AddressBar()
        {
            InitializeComponent();

            // Pasting a path navigates immediately, with no Enter required.
            DataObject.AddPastingHandler(PathTextBox, PathTextBox_Pasting);

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += SearchDebounce_Tick;
        }

        private void PathTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            // Let the paste complete first, then navigate if it produced a valid directory.
            // Deferring avoids racing the TextBox's own text insertion.
            PathTextBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                string targetPath = PathTextBox.Text.Trim();
                if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
                {
                    NavigationRequested?.Invoke(this, targetPath);
                    ExitEditMode();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public string CurrentPath
        {
            get => (string)GetValue(CurrentPathProperty);
            set => SetValue(CurrentPathProperty, value);
        }

        public bool CanGoBack
        {
            get => (bool)GetValue(CanGoBackProperty);
            set => SetValue(CanGoBackProperty, value);
        }

        public bool CanGoForward
        {
            get => (bool)GetValue(CanGoForwardProperty);
            set => SetValue(CanGoForwardProperty, value);
        }

        private static void OnCurrentPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AddressBar bar)
            {
                bar.UpdateBreadcrumbs();
            }
        }

        public void EnterEditMode()
        {
            if (_isEditMode) return;
            if (_isSearchMode) ExitSearchMode();
            _isEditMode = true;

            HideError();

            BreadcrumbsWrapper.Visibility = Visibility.Collapsed;
            EditModeGrid.Visibility = Visibility.Visible;

            PathTextBox.Text = CurrentPath;

            PathTextBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                PathTextBox.Focus();
                PathTextBox.SelectAll();
                Keyboard.Focus(PathTextBox);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ExitEditMode()
        {
            if (!_isEditMode) return;
            _isEditMode = false;

            HideError();

            EditModeGrid.Visibility = Visibility.Collapsed;
            BreadcrumbsWrapper.Visibility = Visibility.Visible;

            UpdateBreadcrumbs();
        }

        // --- Inline index search ----------------------------------------------------

        private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSearchMode)
                ExitSearchMode();
            else
                EnterSearchMode();
        }

        /// <summary>Switches the bar into search mode and puts the cursor in the search box immediately.</summary>
        public void EnterSearchMode()
        {
            if (_isSearchMode) return;
            if (_isEditMode) ExitEditMode();
            _isSearchMode = true;

            HideError();

            BreadcrumbsWrapper.Visibility = Visibility.Collapsed;
            EditModeGrid.Visibility = Visibility.Collapsed;
            SearchModeGrid.Visibility = Visibility.Visible;

            SearchTextBox.Text = string.Empty;
            SearchPlaceholder.Visibility = Visibility.Visible;

            // Activate the caret right away so the user can start typing without a second click.
            SearchTextBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchTextBox.Focus();
                Keyboard.Focus(SearchTextBox);
            }), DispatcherPriority.Background);
        }

        private void ExitSearchMode()
        {
            if (!_isSearchMode) return;
            _isSearchMode = false;

            _searchDebounce.Stop();
            SearchResultsPopup.IsOpen = false;
            SearchResultsList.ItemsSource = null;

            SearchModeGrid.Visibility = Visibility.Collapsed;
            BreadcrumbsWrapper.Visibility = Visibility.Visible;

            UpdateBreadcrumbs();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            _searchDebounce.Stop();
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                SearchResultsPopup.IsOpen = false;
                SearchResultsList.ItemsSource = null;
                return;
            }
            _searchDebounce.Start();
        }

        private async void SearchDebounce_Tick(object sender, EventArgs e)
        {
            _searchDebounce.Stop();
            string query = SearchTextBox.Text;
            if (string.IsNullOrWhiteSpace(query)) return;

            var results = await IndexService.Instance.SearchAsync(query, ResolveActiveDirectory());

            // Drop stale results if the query moved on while we were searching.
            if (!_isSearchMode || !string.Equals(query, SearchTextBox.Text, StringComparison.Ordinal)) return;

            SearchResultsList.ItemsSource = results;
            SearchResultsPopup.IsOpen = results.Count > 0;
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ExitSearchMode();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && SearchResultsPopup.IsOpen && SearchResultsList.Items.Count > 0)
            {
                SearchResultsList.SelectedIndex = 0;
                (SearchResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                ChooseSearchResult((SearchResultsList.SelectedItem
                    ?? SearchResultsList.Items.Cast<object>().FirstOrDefault()) as FolderItem);
                e.Handled = true;
            }
        }

        private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ChooseSearchResult(SearchResultsList.SelectedItem as FolderItem);
            e.Handled = true;
        }

        private void SearchResultsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ChooseSearchResult(SearchResultsList.SelectedItem as FolderItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SearchResultsPopup.IsOpen = false;
                SearchTextBox.Focus();
                e.Handled = true;
            }
        }

        private void ChooseSearchResult(FolderItem item)
        {
            if (item == null) return;
            SearchResultChosen?.Invoke(this, item);
            ExitSearchMode();
        }

        /// <summary>The directory this pane is currently in (falls back to a selected item's parent).</summary>
        private string ResolveActiveDirectory()
        {
            string p = CurrentPath;
            if (string.IsNullOrEmpty(p)) return null;
            if (Directory.Exists(p)) return p;
            try { return Path.GetDirectoryName(p); }
            catch { return null; }
        }

        /// <summary>
        /// Auto-dismisses search mode once the user is no longer searching, i.e. keyboard
        /// focus has left the search box, the results list, and the toggle button.
        /// </summary>
        private void Search_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_isSearchMode) return;
            if (IsWithinSearchUi(e.NewFocus)) return;
            ExitSearchMode();
        }

        private bool IsWithinSearchUi(IInputElement element)
        {
            DependencyObject d = element as DependencyObject;
            while (d != null)
            {
                if (d == SearchTextBox || d == SearchResultsList
                    || d == SearchResultsPopup || d == SearchToggleButton)
                    return true;

                DependencyObject parent = (d is Visual) ? VisualTreeHelper.GetParent(d) : null;
                d = parent ?? LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void ShowError(string message)
        {
            ErrorTextBlock.Text = message;
            ErrorTextBlock.Visibility = Visibility.Visible;

            // Error background tint and border, sourced from the active theme palette.
            MainBarBorder.BorderBrush = ThemeService.Brush("AppErrorBrush");
            MainBarBorder.Background = ThemeService.Brush("AppErrorBgBrush");
        }

        private void HideError()
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            // Re-establish the dynamic-resource bindings so the bar keeps following the theme.
            MainBarBorder.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
            MainBarBorder.SetResourceReference(Border.BackgroundProperty, "AppPanelDeepBrush");
        }

        private void BreadcrumbsWrapper_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // If they clicked the wrapper directly or elements that aren't buttons, enter edit mode
            if (e.OriginalSource is Border || e.OriginalSource is StackPanel || e.OriginalSource is TextBlock)
            {
                EnterEditMode();
                e.Handled = true;
            }
        }

        private void BreadcrumbsWrapper_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateBreadcrumbs();
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string targetPath = PathTextBox.Text.Trim();
                if (string.IsNullOrEmpty(targetPath))
                {
                    ExitEditMode();
                    return;
                }

                if (Directory.Exists(targetPath))
                {
                    NavigationRequested?.Invoke(this, targetPath);
                    ExitEditMode();
                }
                else
                {
                    ShowError("Path does not exist");
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ExitEditMode();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ExitEditMode();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            HideError();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            GoBackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            GoForwardRequested?.Invoke(this, EventArgs.Empty);
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            HomeRequested?.Invoke(this, EventArgs.Empty);
        }

        private void HomeButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            SetHomeRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void SegmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                NavigationRequested?.Invoke(this, path);
            }
        }

        private void OverflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is string path)
            {
                NavigationRequested?.Invoke(this, path);
            }
        }

        private void UpdateBreadcrumbs()
        {
            if (_isEditMode) return;

            BreadcrumbsContainer.Children.Clear();
            string path = CurrentPath;
            if (string.IsNullOrEmpty(path)) return;

            var segments = GetSegments(path);
            if (segments.Count == 0) return;

            double availableWidth = BreadcrumbsWrapper.ActualWidth;
            if (availableWidth <= 0)
            {
                // Fallback if size not computed yet
                foreach (var seg in segments)
                {
                    AddSegmentUI(seg);
                }
                return;
            }

            var elementPairs = new List<BreadcrumbUIPair>();
            double totalWidth = 0;

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var pair = new BreadcrumbUIPair();

                if (seg.IsLast)
                {
                    pair.SegmentElement = CreateLastSegmentTextBlock(seg);
                }
                else
                {
                    pair.SegmentElement = CreateSegmentButton(seg);
                }

                pair.SegmentElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                pair.Width = pair.SegmentElement.DesiredSize.Width;

                if (i < segments.Count - 1)
                {
                    pair.ChevronElement = CreateChevron();
                    pair.ChevronElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    pair.Width += pair.ChevronElement.DesiredSize.Width;
                }

                elementPairs.Add(pair);
                totalWidth += pair.Width;
            }

            if (totalWidth <= availableWidth)
            {
                foreach (var pair in elementPairs)
                {
                    BreadcrumbsContainer.Children.Add(pair.SegmentElement);
                    if (pair.ChevronElement != null)
                    {
                        BreadcrumbsContainer.Children.Add(pair.ChevronElement);
                    }
                }
            }
            else
            {
                // Measure the overflow dot button and chevron
                var dotDotDotButton = CreateOverflowButton(new List<BreadcrumbSegment>());
                dotDotDotButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double dotDotDotWidth = dotDotDotButton.DesiredSize.Width;

                var dotDotDotChevron = CreateChevron();
                dotDotDotChevron.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double dotDotDotChevronWidth = dotDotDotChevron.DesiredSize.Width;

                double reservedWidth = dotDotDotWidth + dotDotDotChevronWidth;

                int k = segments.Count - 1; // Always show at least the last segment
                double currentRightWidth = elementPairs[k].Width;

                for (int i = segments.Count - 2; i >= 0; i--)
                {
                    double nextWidth = currentRightWidth + elementPairs[i].Width;
                    if (nextWidth + reservedWidth <= availableWidth)
                    {
                        k = i;
                        currentRightWidth = nextWidth;
                    }
                    else
                    {
                        break;
                    }
                }

                var hiddenSegments = new List<BreadcrumbSegment>();
                for (int i = 0; i < k; i++)
                {
                    hiddenSegments.Add(segments[i]);
                }

                var finalDotDotDotButton = CreateOverflowButton(hiddenSegments);
                BreadcrumbsContainer.Children.Add(finalDotDotDotButton);
                BreadcrumbsContainer.Children.Add(CreateChevron());

                for (int i = k; i < segments.Count; i++)
                {
                    var pair = elementPairs[i];
                    BreadcrumbsContainer.Children.Add(pair.SegmentElement);
                    if (pair.ChevronElement != null)
                    {
                        BreadcrumbsContainer.Children.Add(pair.ChevronElement);
                    }
                }
            }
        }

        private void AddSegmentUI(BreadcrumbSegment seg)
        {
            if (seg.IsLast)
            {
                BreadcrumbsContainer.Children.Add(CreateLastSegmentTextBlock(seg));
            }
            else
            {
                BreadcrumbsContainer.Children.Add(CreateSegmentButton(seg));
                BreadcrumbsContainer.Children.Add(CreateChevron());
            }
        }

        private Button CreateSegmentButton(BreadcrumbSegment segment)
        {
            var btn = new Button
            {
                Content = segment.Name,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(1, 0, 1, 0),
                Tag = segment.FullPath,
                Style = (Style)FindResource("BreadcrumbButtonStyle")
            };
            btn.Click += SegmentButton_Click;
            return btn;
        }

        private TextBlock CreateLastSegmentTextBlock(BreadcrumbSegment segment)
        {
            return new TextBlock
            {
                Text = segment.Name,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(1, 0, 1, 0),
                Foreground = (Brush)FindResource("BreadcrumbActiveTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            };
        }

        private UIElement CreateChevron()
        {
            return new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronRight16,
                FontSize = 8,
                Foreground = (Brush)FindResource("BreadcrumbChevronBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
        }

        private Button CreateOverflowButton(List<BreadcrumbSegment> hiddenSegments)
        {
            var btn = new Button
            {
                Content = "…",
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(1, 0, 1, 0),
                Style = (Style)FindResource("BreadcrumbButtonStyle")
            };

            var menu = new ContextMenu();
            foreach (var seg in hiddenSegments)
            {
                var item = new MenuItem { Header = seg.Name, Tag = seg.FullPath };
                item.Click += OverflowMenuItem_Click;
                menu.Items.Add(item);
            }

            btn.ContextMenu = menu;
            btn.Click += (s, e) =>
            {
                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };

            return btn;
        }

        private List<BreadcrumbSegment> GetSegments(string path)
        {
            var list = new List<BreadcrumbSegment>();
            if (string.IsNullOrEmpty(path)) return list;

            path = path.Replace('/', '\\');
            var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return list;

            string accumulated = "";
            bool isUNC = path.StartsWith("\\\\");

            if (isUNC)
            {
                if (parts.Length >= 2)
                {
                    string serverAndShare = "\\\\" + parts[0] + "\\" + parts[1];
                    list.Add(new BreadcrumbSegment
                    {
                        Name = parts[0] + "\\" + parts[1],
                        FullPath = serverAndShare
                    });
                    accumulated = serverAndShare;

                    for (int i = 2; i < parts.Length; i++)
                    {
                        accumulated = Path.Combine(accumulated, parts[i]);
                        list.Add(new BreadcrumbSegment
                        {
                            Name = parts[i],
                            FullPath = accumulated
                        });
                    }
                }
            }
            else
            {
                string drive = parts[0];
                if (!drive.EndsWith(":") && drive.Length == 1)
                {
                    drive += ":";
                }

                accumulated = drive + "\\";
                list.Add(new BreadcrumbSegment
                {
                    Name = drive,
                    FullPath = accumulated
                });

                for (int i = 1; i < parts.Length; i++)
                {
                    accumulated = Path.Combine(accumulated, parts[i]);
                    list.Add(new BreadcrumbSegment
                    {
                        Name = parts[i],
                        FullPath = accumulated
                    });
                }
            }

            if (list.Count > 0)
            {
                list[list.Count - 1].IsLast = true;
            }

            return list;
        }

        private class BreadcrumbSegment
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public bool IsLast { get; set; }
        }

        private class BreadcrumbUIPair
        {
            public UIElement SegmentElement { get; set; }
            public UIElement ChevronElement { get; set; }
            public double Width { get; set; }
        }
    }
}
