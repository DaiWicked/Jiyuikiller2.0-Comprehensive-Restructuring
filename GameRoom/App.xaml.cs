using System.Windows;

namespace GameRoom
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            Theme.Apply();
        }
    }
}
