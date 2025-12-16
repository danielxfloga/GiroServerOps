using System;
using System.Windows;

namespace GiroServerOps
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (DbUdl.TryEnsureValidUdl(out _))
            {
                var mw = new MainWindow();
                MainWindow = mw;
                mw.Show();
                return;
            }

            var cfg = new DbConfigWindow();
            cfg.UdlReady += (_, __) =>
            {
                var mw = new MainWindow();
                MainWindow = mw;
                mw.Show();
                cfg.Close();
            };
            cfg.Show();
        }
    }
}
