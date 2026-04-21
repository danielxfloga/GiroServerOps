using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NetcoServerConsole
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
        private int _contentLoadVersion;

        public MainWindow()
        {
            InitializeComponent();
            StateChanged += MainWindow_StateChanged;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;

            ShowStartupOverlay("Cargando monitoreo...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await ShowDashboardViewAsync();
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
            if (e.ChangedButton != MouseButton.Left)
                return;

            var source = e.OriginalSource as DependencyObject;
            if (!IsInteractiveTopBarElement(source))
                DragMove();
        }

        private static bool IsInteractiveTopBarElement(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase || source is TextBox)
                    return true;

                source = LogicalTreeHelper.GetParent(source) ?? System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            return false;
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

        private async void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            CloseMenuAndToggle();

            if (sender == btnMenuDatabase)
                ShowDatabaseQueryView();
            else if (sender == btnMenuMonitoring)
                await ShowDashboardViewAsync();
        }

        private async Task ShowDashboardViewAsync()
        {
            var loadVersion = ++_contentLoadVersion;

            ShowStartupOverlay("Cargando monitoreo...");
            DisposeHostedContent();

            try
            {
                var dashboardView = new DashboardView();
                MainContentHost.Content = dashboardView;

                await dashboardView.InitializeAsync();
            }
            finally
            {
                if (loadVersion == _contentLoadVersion)
                    HideStartupOverlay();
            }
        }

        private void ShowDatabaseQueryView()
        {
            _contentLoadVersion++;
            DisposeHostedContent();
            MainContentHost.Content = new QuerySqlView();
            HideStartupOverlay();
        }

        private void DisposeHostedContent()
        {
            if (MainContentHost.Content is IDisposable disposable)
                disposable.Dispose();

            MainContentHost.Content = null;
        }

        private void ShowStartupOverlay(string status)
        {
            txtStartupStatus.Text = status;
            if (imgStartupLogo.Source == null)
                imgStartupLogo.Source = AppMemoryCoordinator.CreateTransientStartupLogo();
            StartupOverlay.Visibility = Visibility.Visible;
        }

        private void HideStartupOverlay()
        {
            StartupOverlay.Visibility = Visibility.Collapsed;
            AppMemoryCoordinator.ScheduleIdleTrim(() =>
            {
                imgStartupLogo.Source = null;
            });
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            DisposeHostedContent();
            imgStartupLogo.Source = null;
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
