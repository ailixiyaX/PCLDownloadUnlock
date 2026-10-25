using System.Windows;

namespace PCLDownloadUnlock
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (Cli.LooksLikeCli(e.Args))
            {
                var code = Cli.Run(e.Args);
                Shutdown(code);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
    }
}
