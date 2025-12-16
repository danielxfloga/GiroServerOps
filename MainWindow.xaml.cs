using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace GiroServerOps
{
    public enum KpiStatus
    {
        Ok,
        Warning,
        Critical
    }
    public partial class MainWindow : Window
    {
        private bool _isClosing;

        public MainWindow()
        {
            InitializeComponent();
            StateChanged += MainWindow_StateChanged;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            Root.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(22);
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void btnMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void btnMax_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void btnHamburger_Click(object sender, RoutedEventArgs e)
        {
            if (btnHamburger.IsChecked == true) OpenMenu();
            else CloseMenu();
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CloseMenuAndToggle();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                CloseMenuAndToggle();
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            CloseMenuAndToggle();
        }

        private void CloseMenuAndToggle()
        {
            if (btnHamburger.IsChecked == true)
                btnHamburger.IsChecked = false;

            CloseMenu();
        }

        private void OpenMenu()
        {
            if (_isClosing) return;

            Overlay.IsHitTestVisible = true;

            ((Storyboard)Resources["OverlayShow"]).Begin(this, true);
            ((Storyboard)Resources["CurtainOpen"]).Begin(this, true);
        }

        private void CloseMenu()
        {
            if (_isClosing) return;

            if (CurtainHost.Visibility != Visibility.Visible && Overlay.Visibility != Visibility.Visible)
                return;

            _isClosing = true;

            var overlayHide = (Storyboard)Resources["OverlayHide"];
            var curtainClose = (Storyboard)Resources["CurtainClose"];

            curtainClose.Completed += CurtainClose_Completed;

            overlayHide.Begin(this, true);
            curtainClose.Begin(this, true);
        }

        private void CurtainClose_Completed(object? sender, EventArgs e)
        {
            var sb = sender as Storyboard;
            if (sb != null)
                sb.Completed -= CurtainClose_Completed;

            Overlay.IsHitTestVisible = false;
            _isClosing = false;
        }

        private void btnHamburger_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
