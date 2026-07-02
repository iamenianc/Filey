using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExcelDataReader;

namespace Filey.Previews
{
    public partial class ExcelPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // unused

        private string _currentFilePath;
        private CancellationTokenSource _cts;

        public ExcelPreview()
        {
            InitializeComponent();
        }

        public void Preview(string filePath, CancellationToken token)
        {
            _currentFilePath = filePath;
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var linkedToken = _cts.Token;

            Task.Run(() => LoadExcelFileAsync(filePath, linkedToken), linkedToken);
        }

        public void Unload()
        {
            _cts?.Cancel();
            _cts = null;

            SpreadsheetTabControl.Items.Clear();
            SpreadsheetPaneHost.Content = null;
        }

        private async Task LoadExcelFileAsync(string filePath, CancellationToken token)
        {
            try
            {
                byte[] decryptedBytes = null;
                if (IsEncryptedExcel(filePath))
                {
                    bool passwordCancelled = false;
                    string errorMessage = null;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            Window owner = Window.GetWindow(this);
                            decryptedBytes = TryDecryptExcelWithPrompt(filePath, owner);
                        }
                        catch (OperationCanceledException)
                        {
                            passwordCancelled = true;
                        }
                        catch (Exception ex)
                        {
                            errorMessage = ex.Message;
                        }
                    });

                    if (passwordCancelled)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Decryption Cancelled", "Password is required to view this file."));
                        });
                        return;
                    }

                    if (errorMessage != null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error Decrypting Spreadsheet", errorMessage));
                        });
                        return;
                    }
                }

                if (token.IsCancellationRequested) return;

                DataSet dataSet = await Task.Run(() => LoadExcelDataSet(filePath, decryptedBytes), token);

                if (token.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _currentFilePath != filePath) return;

                    bool isFullWindow = false;
                    Window parentWindow = Window.GetWindow(this);
                    if (parentWindow is PreviewWindow)
                    {
                        isFullWindow = true;
                    }
                    else
                    {
                        DependencyObject parent = VisualTreeHelper.GetParent(this);
                        while (parent != null)
                        {
                            if (parent is PreviewWindow)
                            {
                                isFullWindow = true;
                                break;
                            }
                            parent = VisualTreeHelper.GetParent(parent);
                        }
                    }

                    SpreadsheetMetadataLabel.Text = $"{Path.GetFileName(filePath)} (Safe, Non-Executable)";

                    if (SpreadsheetHeaderBorder != null)
                    {
                        SpreadsheetHeaderBorder.Visibility = isFullWindow ? Visibility.Collapsed : Visibility.Visible;
                    }

                    if (!isFullWindow)
                    {
                        SpreadsheetTabControl.Visibility = Visibility.Collapsed;
                        SpreadsheetPaneModeGrid.Visibility = Visibility.Visible;
                        OpenNewWindowButton.Visibility = Visibility.Visible;

                        if (dataSet.Tables.Count > 0)
                        {
                            DataTable firstTable = dataSet.Tables[0];
                            DataTable truncated = TruncateDataTable(firstTable, 60, 26);
                            DataGrid grid = CreateSpreadsheetDataGrid(truncated, false);
                            SpreadsheetPaneHost.Content = grid;
                        }
                    }
                    else
                    {
                        SpreadsheetPaneModeGrid.Visibility = Visibility.Collapsed;
                        SpreadsheetTabControl.Visibility = Visibility.Visible;
                        OpenNewWindowButton.Visibility = Visibility.Collapsed;
                        SpreadsheetTabControl.Items.Clear();

                        foreach (DataTable table in dataSet.Tables)
                        {
                            TabItem tabItem = new TabItem
                            {
                                Header = table.TableName
                            };
                            DataGrid grid = CreateSpreadsheetDataGrid(table, true);
                            tabItem.Content = grid;
                            SpreadsheetTabControl.Items.Add(tabItem);
                        }

                        if (SpreadsheetTabControl.Items.Count > 0)
                        {
                            SpreadsheetTabControl.SelectedIndex = 0;
                        }
                    }

                    string sizeStr = GetFormattedFileSize(filePath);
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Excel Spreadsheet", sizeStr));
                });
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error Loading Spreadsheet", ex.Message));
                });
            }
        }

        private static bool IsEncryptedExcel(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] head = new byte[8];
                    int read = fs.Read(head, 0, 8);
                    if (read < 8) return false;
                    byte[] ole2Magic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
                    for (int i = 0; i < 8; i++)
                    {
                        if (head[i] != ole2Magic[i]) return false;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static byte[] TryDecryptExcelWithPrompt(string filePath, Window ownerWindow)
        {
            while (true)
            {
                string enteredPassword = null;
                bool cancelled = false;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dialog = new PasswordPromptDialog("Decrypt Spreadsheet", $"Enter password for \"{Path.GetFileName(filePath)}\":")
                    {
                        Owner = ownerWindow
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        enteredPassword = dialog.Password;
                    }
                    else
                    {
                        cancelled = true;
                    }
                });

                if (cancelled)
                {
                    throw new OperationCanceledException("Password entry cancelled by user.");
                }

                try
                {
                    return ExcelDecryptor.Decrypt(filePath, enteredPassword);
                }
                catch (ExcelDecryptException ex) when (ex.WrongPassword)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("Incorrect password. Please try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                catch (Exception ex)
                {
                    throw new Exception($"Decryption failed: {ex.Message}", ex);
                }
            }
        }

        private static DataSet LoadExcelDataSet(string filePath, byte[] decryptedBytes)
        {
            Stream stream = decryptedBytes != null
                ? (Stream)new MemoryStream(decryptedBytes)
                : new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using (stream)
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                return reader.AsDataSet(new ExcelDataSetConfiguration()
                {
                    ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                    {
                        UseHeaderRow = false
                    }
                });
            }
        }

        private static DataTable TruncateDataTable(DataTable originalTable, int maxRows, int maxCols)
        {
            var truncated = new DataTable(originalTable.TableName);
            int colsToKeep = Math.Min(originalTable.Columns.Count, maxCols);
            for (int i = 0; i < colsToKeep; i++)
            {
                truncated.Columns.Add(originalTable.Columns[i].ColumnName, originalTable.Columns[i].DataType);
            }

            int rowsToKeep = Math.Min(originalTable.Rows.Count, maxRows);
            for (int i = 0; i < rowsToKeep; i++)
            {
                DataRow newRow = truncated.NewRow();
                for (int j = 0; j < colsToKeep; j++)
                {
                    newRow[j] = originalTable.Rows[i][j];
                }
                truncated.Rows.Add(newRow);
            }
            return truncated;
        }

        private static string GetExcelColumnName(int columnIndex)
        {
            int dividend = columnIndex;
            string columnName = string.Empty;
            while (dividend > 0)
            {
                int modifier = (dividend - 1) % 26;
                columnName = Convert.ToChar(65 + modifier).ToString() + columnName;
                dividend = (dividend - modifier) / 26;
            }
            return columnName;
        }

        private DataGrid CreateSpreadsheetDataGrid(DataTable dataTable, bool isFullWindow)
        {
            int headerRowIndex = 0;
            int maxNonNullCount = -1;
            int maxRowsToScan = Math.Min(20, dataTable.Rows.Count);

            for (int r = 0; r < maxRowsToScan; r++)
            {
                DataRow row = dataTable.Rows[r];
                int nonNullCount = 0;
                for (int c = 0; c < dataTable.Columns.Count; c++)
                {
                    object val = row[c];
                    if (val != null && val != DBNull.Value && !string.IsNullOrWhiteSpace(val.ToString()))
                    {
                        nonNullCount++;
                    }
                }
                if (nonNullCount > maxNonNullCount)
                {
                    maxNonNullCount = nonNullCount;
                    headerRowIndex = r;
                }
            }

            int lastUsedRowIndex = headerRowIndex;
            int lastUsedColIndex = 0;

            for (int r = headerRowIndex; r < dataTable.Rows.Count; r++)
            {
                DataRow row = dataTable.Rows[r];
                bool rowHasValue = false;
                for (int c = 0; c < dataTable.Columns.Count; c++)
                {
                    object val = row[c];
                    if (val != null && val != DBNull.Value && !string.IsNullOrWhiteSpace(val.ToString()))
                    {
                        rowHasValue = true;
                        if (c > lastUsedColIndex)
                        {
                            lastUsedColIndex = c;
                        }
                    }
                }
                if (rowHasValue)
                {
                    lastUsedRowIndex = r;
                }
            }

            int columnsCount = Math.Max(1, lastUsedColIndex + 1);
            int rowsCount = dataTable.Rows.Count > 0 ? (lastUsedRowIndex + 1) : 0;

            var cleanedTable = new DataTable(dataTable.TableName);
            for (int c = 0; c < columnsCount; c++)
            {
                cleanedTable.Columns.Add("Col" + c, dataTable.Columns[c].DataType);
            }

            int startRow = dataTable.Rows.Count > headerRowIndex ? headerRowIndex : dataTable.Rows.Count;
            for (int r = startRow; r < rowsCount; r++)
            {
                DataRow newRow = cleanedTable.NewRow();
                for (int c = 0; c < columnsCount; c++)
                {
                    newRow[c] = dataTable.Rows[r][c];
                }
                cleanedTable.Rows.Add(newRow);
            }

            double initialScale = isFullWindow ? 1.0 : 0.8;
            var grid = new DataGrid
            {
                Style = TryFindResource("SpreadsheetDataGridStyle") as Style,
                HorizontalScrollBarVisibility = isFullWindow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = isFullWindow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                LayoutTransform = new ScaleTransform(initialScale, initialScale)
            };

            grid.AutoGeneratingColumn += (s, e) =>
            {
                if (s is DataGrid dg)
                {
                    int index = dg.Columns.Count;
                    e.Column.Header = GetExcelColumnName(index + 1);

                    if (e.Column is DataGridTextColumn textColumn && textColumn.Binding is System.Windows.Data.Binding binding)
                    {
                        binding.Converter = new ExcelDateConverter();
                    }
                }
            };

            grid.LoadingRow += (s, e) =>
            {
                int rowIndex = e.Row.GetIndex();
                e.Row.Header = (rowIndex + 1).ToString();
                if (rowIndex == 0)
                {
                    e.Row.FontWeight = FontWeights.Bold;
                    e.Row.Background = Brushes.Black;
                    e.Row.Foreground = Brushes.White;
                }
                else
                {
                    e.Row.ClearValue(Control.FontWeightProperty);
                    e.Row.ClearValue(Control.BackgroundProperty);
                    e.Row.ClearValue(Control.ForegroundProperty);
                }
            };

            grid.PreviewMouseWheel += DataGrid_PreviewMouseWheel;
            grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, DataGrid_CopyCommandExecuted));
            grid.ItemsSource = cleanedTable.DefaultView;
            return grid;
        }

        private void DataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (sender is DataGrid grid)
                {
                    double currentScaleVal = 1.0;
                    if (grid.LayoutTransform is ScaleTransform scale)
                    {
                        currentScaleVal = scale.ScaleX;
                    }

                    double step = 0.10;
                    double newScale = currentScaleVal + (e.Delta > 0 ? step : -step);
                    newScale = Math.Max(0.5, Math.Min(4.0, newScale));

                    if (grid.LayoutTransform is ScaleTransform mutableScale && !mutableScale.IsFrozen)
                    {
                        mutableScale.ScaleX = newScale;
                        mutableScale.ScaleY = newScale;
                    }
                    else
                    {
                        grid.LayoutTransform = new ScaleTransform(newScale, newScale);
                    }
                }
            }
            else
            {
                if (sender is DataGrid grid && grid.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                {
                    e.Handled = true;
                }
            }
        }

        private void DataGrid_CopyCommandExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is DataGrid grid)
            {
                var selectedCells = grid.SelectedCells;
                if (selectedCells.Count == 0) return;

                var rowGroups = new Dictionary<object, List<DataGridCellInfo>>();
                foreach (var cell in selectedCells)
                {
                    if (cell.Item != null)
                    {
                        if (!rowGroups.TryGetValue(cell.Item, out var list))
                        {
                            list = new List<DataGridCellInfo>();
                            rowGroups[cell.Item] = list;
                        }
                        list.Add(cell);
                    }
                }

                var sortedRows = new List<object>(rowGroups.Keys);
                sortedRows.Sort((r1, r2) => grid.Items.IndexOf(r1).CompareTo(grid.Items.IndexOf(r2)));

                var sb = new StringBuilder();
                foreach (var rowItem in sortedRows)
                {
                    var cellsInRow = rowGroups[rowItem];
                    cellsInRow.Sort((c1, c2) => c1.Column.DisplayIndex.CompareTo(c2.Column.DisplayIndex));

                    var cellTexts = new List<string>();
                    foreach (var cellInfo in cellsInRow)
                    {
                        if (cellInfo.Item is DataRowView rowView && cellInfo.Column != null)
                        {
                            int colIndex = grid.Columns.IndexOf(cellInfo.Column);
                            if (colIndex >= 0 && colIndex < rowView.Row.ItemArray.Length)
                            {
                                var cellValue = rowView.Row.ItemArray[colIndex];
                                if (cellValue is DateTime dt)
                                {
                                    string format = (dt.TimeOfDay != TimeSpan.Zero) ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd";
                                    cellTexts.Add(dt.ToString(format));
                                }
                                else
                                {
                                    cellTexts.Add(cellValue?.ToString() ?? string.Empty);
                                }
                            }
                        }
                    }
                    sb.AppendLine(string.Join("\t", cellTexts));
                }

                try
                {
                    Clipboard.SetText(sb.ToString());
                }
                catch
                {
                }
            }
        }

        private void OpenNewWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

            try
            {
                var previewWindow = new PreviewWindow(_currentFilePath)
                {
                    Owner = Window.GetWindow(this)
                };
                previewWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open preview window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);
    }
}
