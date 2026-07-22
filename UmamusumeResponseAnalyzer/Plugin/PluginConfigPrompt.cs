using UmamusumeResponseAnalyzer.LiveDisplay;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal static class PluginConfigPrompt
    {
        public static Task RunAsync(IPlugin plugin)
            => LiveDisplayConsole.RunAsync(() =>
            {
                PluginManager.EnterDispatch();
                try
                {
                    using var callback = PluginManager.EnterPluginCallbackScope();
                    plugin.ConfigPromptAsync().GetAwaiter().GetResult();
                    return Task.CompletedTask;
                }
                finally
                {
                    PluginManager.ExitDispatch();
                }
            });
    }
}
