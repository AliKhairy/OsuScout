using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OsuScoutNew
{
    // MessageBox in the app's own theme. Windows draws MessageBox itself, in its light style,
    // so it can't be restyled; this takes the same arguments and returns the same results.
    public partial class MessageDialog : Window
    {
        private MessageBoxResult _result;

        private MessageDialog(string message, string title, MessageBoxButton buttons)
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;

            if (buttons == MessageBoxButton.YesNo)
            {
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isCancel: false);
                AddButton("No", MessageBoxResult.No, isDefault: false, isCancel: true);
                _result = MessageBoxResult.No; // closing the window answers "no"
            }
            else
            {
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                _result = MessageBoxResult.OK;
            }

            // Ctrl+C copies the message, as it does in a Windows message box (handy for crash logs).
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Clipboard.SetText($"{title}\n\n{message}")));
        }

        // icon is accepted so call sites read like MessageBox.Show; the title says what kind of message it is.
        public static MessageBoxResult Show(Window owner, string message, string title = "Scoutsu",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            var dialog = new MessageDialog(message, title, buttons);
            // A window that hasn't been shown yet (e.g. during start-up) can't own another.
            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._result;
        }

        private void AddButton(string text, MessageBoxResult result, bool isDefault, bool isCancel)
        {
            var button = new Button
            {
                Content = text,
                Width = 90,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel,
                Style = (Style)FindResource(isDefault ? "PrimaryButton" : "SecondaryButton")
            };
            button.Click += (_, _) =>
            {
                _result = result;
                Close();
            };
            ButtonRow.Children.Add(button);
            if (isDefault) Loaded += (_, _) => button.Focus();
        }

        // Same guard as MainWindow.Window_MouseDown: DragMove throws once the button is up.
        private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }
}
