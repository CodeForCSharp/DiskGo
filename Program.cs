namespace DiskGo;

/// <summary>
/// Application entry point. Launches the WinUI 3 window when started with no arguments,
/// or delegates to <see cref="CliRunner"/> when command-line options are provided.
/// </summary>
internal static class CommandLine
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || Array.TrueForAll(args, static arg => arg is "--running-as"))
        {
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            return 0;
        }

        return CliRunner.Run(args);
    }
}
