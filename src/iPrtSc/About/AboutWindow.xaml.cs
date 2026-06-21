using System;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace iPrtSc;

public partial class AboutWindow : Window
{
    public AboutWindow(string? updateVersion = null)
    {
        InitializeComponent();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        CopyrightText.Text = $"© {DateTime.Now.Year} iPrtSc";

        if (!string.IsNullOrEmpty(updateVersion))
        {
            UpdateBanner.Content = $"Update available: v{updateVersion} — Download";
            UpdateBanner.Visibility = Visibility.Visible;
        }
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(UpdateChecker.ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log("AboutWindow.OnUpdateClick", ex); }
        Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log("AboutWindow.OnNavigate", ex); }
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
