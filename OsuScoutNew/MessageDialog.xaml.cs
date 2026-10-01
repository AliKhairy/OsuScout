using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OsuScoutNew.Services;

namespace OsuScoutNew
{
    // MessageBox in the app's own theme. Windows draws MessageBox itself, in its light style,
    // so it can't be restyled; this takes the same arguments and returns the same results.
    public partial class MessageDialog : Window
    {
        private MessageBoxResult _result;

        private MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
            ShowBadge(icon);

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

        public static MessageBoxResult Show(Window owner, string message, string title = "Scoutsu",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            var dialog = new MessageDialog(message, title, buttons, icon);
            // A window that hasn't been shown yet (e.g. during start-up) can't own another.
            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._result;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SystemInteropService.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private void AddButton(string text, MessageBoxResult result, bool isDefault, bool isCancel)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (isDefault) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) =>
            {
                _result = result;
                Close();
            };
            ButtonRow.Children.Add(button);
            if (isDefault) Loaded += (_, _) => button.Focus();
        }

        private void ShowBadge(MessageBoxImage icon)
        {
            (string text, string brush) = icon switch
            {
                MessageBoxImage.Error => ("!", "Brush.AccentStrong"),
                MessageBoxImage.Warning => ("!", "Brush.Field"),
                MessageBoxImage.Question => ("?", "Brush.Field"),
                MessageBoxImage.Information => ("i", "Brush.Field"),
                _ => (null, null)
            };
            if (text == null) return;

            BadgeText.Text = text;
            BadgeText.Foreground = icon == MessageBoxImage.Error ? Brushes.White : (Brush)FindResource("Brush.Accent");
            BadgeCircle.Fill = (Brush)FindResource(brush);
            Badge.Visibility = Visibility.Visible;
        }
    }
}
