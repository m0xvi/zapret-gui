using System.Windows;

namespace ZapretGui.Views
{
    public partial class InputDialog : Window
    {
        public string Value => ValueBox.Text.Trim();
        public string SecondaryValue => SecondaryValueBox.Text.Trim();
        public bool IsChecked => CheckBoxInput.IsChecked == true;

        public InputDialog(string title, string prompt, string? secondaryPrompt = null,
            string checkBoxText = "", string initialValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            ValueBox.Text = initialValue;
            ValueBox.SelectAll();

            if (!string.IsNullOrWhiteSpace(secondaryPrompt))
            {
                SecondaryPromptText.Text = secondaryPrompt;
                SecondaryPromptText.Visibility = Visibility.Visible;
                SecondaryValueBox.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(checkBoxText))
            {
                CheckBoxInput.Content = checkBoxText;
                CheckBoxInput.Visibility = Visibility.Visible;
            }
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Value))
            {
                ValueBox.Focus();
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
