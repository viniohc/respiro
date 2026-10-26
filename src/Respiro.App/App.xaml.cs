using System.Windows;
namespace Respiro.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Legacy data stays at its original location; all stores share RespiroDataPaths.
        base.OnStartup(e);
        ThemeManager.Apply(UiSettings.Load().Theme);
    }
}
