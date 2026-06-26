using System.Windows;
using System.Windows.Input;

namespace Filey
{
    /// <summary>
    /// Interaction logic for RenameDialog.xaml
    /// </summary>
    public partial class RenameDialog : Window
    {
        public string NewName { get; private set; }

        public RenameDialog(string currentName)
        {
            InitializeComponent();
            NameTextBox.Text = currentName;

            Loaded += RenameDialog_Loaded;
        }

        private void RenameDialog_Loaded(object sender, RoutedEventArgs e)
        {
            NameTextBox.Focus();

            // Select only the name before extension
            string text = NameTextBox.Text;
            int lastDot = text.LastIndexOf('.');
            if (lastDot > 0)
            {
                NameTextBox.Select(0, lastDot);
            }
            else
            {
                NameTextBox.SelectAll();
            }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void RenameBtn_Click(object sender, RoutedEventArgs e)
        {
            Submit();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
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
            string name = NameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Name cannot be empty.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            NewName = name;
            DialogResult = true;
            Close();
        }
    }
}
