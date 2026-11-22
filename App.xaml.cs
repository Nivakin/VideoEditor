using System.Windows;

namespace VideoEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selfcheck"))
        {
            try { Cutter.SelfCheck(); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
    }
}
