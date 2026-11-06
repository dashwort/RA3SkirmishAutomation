using Microsoft.UI.Xaml;

namespace RA3SkirmishLauncher
{
    public partial class App : Application
    {
        private Window? window;

        public App()
        {
            UnhandledException += (_, error) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), error.Exception.ToString());
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                window = new MainWindow();
                window.Activate();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), error.ToString());
                throw;
            }
        }
    }
}
