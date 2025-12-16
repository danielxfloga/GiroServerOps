using System;
using System.Windows;
using System.Windows.Input;

namespace GiroServerOps
{
    public partial class DbConfigWindow : Window
    {
        public event EventHandler? UdlReady;

        public DbConfigWindow()
        {
            InitializeComponent();

            if (rbWindows != null) rbWindows.Checked += AuthChanged;
            if (rbSql != null) rbSql.Checked += AuthChanged;

            if (txtUdlPath != null) txtUdlPath.Text = DbUdl.UdlPath;
            if (txtInstance != null) txtInstance.Text = @".\GIRO";
            if (txtDatabase != null) txtDatabase.Text = "master";

            RefreshAuthUI();
        }



        private void AuthChanged(object sender, RoutedEventArgs e)
        {
            RefreshAuthUI();
        }

        private void RefreshAuthUI()
        {
            if (rbSql == null || txtUser == null || txtPass == null)
                return;

            var sql = rbSql.IsChecked == true;
            txtUser.IsEnabled = sql;
            txtPass.IsEnabled = sql;
        }


        private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private string BuildConnectionString()
        {
            var instance = txtInstance.Text;
            var db = txtDatabase.Text;

            if (rbWindows.IsChecked == true)
                return DbUdl.BuildWindowsAuth(instance, db);

            return DbUdl.BuildSqlAuth(instance, db, txtUser.Text, txtPass.Password);
        }

        private void Test_Click(object sender, RoutedEventArgs e)
        {
            var cs = BuildConnectionString();
            var ok = DbUdl.TestConnection(cs);

            txtStatus.Text = ok ? "Conexión exitosa." : "Falló la conexión. Revisa instancia/BD/credenciales.";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var cs = BuildConnectionString();
            var ok = DbUdl.TryWriteUdlIfConnectionOk(cs);

            if (!ok)
            {
                txtStatus.Text = "No se generó el .udl porque la conexión falló.";
                return;
            }

            txtStatus.Text = "Conexión exitosa. .udl generado.";
            UdlReady?.Invoke(this, EventArgs.Empty);
        }
    }
}
