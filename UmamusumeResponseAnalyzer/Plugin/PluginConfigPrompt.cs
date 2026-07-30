using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal static class PluginConfigPrompt
    {
        public static async Task RunAsync(IPlugin plugin, CancellationToken cancellationToken = default)
        {
            using var callback = await PluginManager.EnterPluginCallbackAsync(cancellationToken);
            if (!PluginManager.IsLoadedPluginInstance(plugin))
                throw new InvalidOperationException($"插件已卸载，无法打开配置：{PluginManager.InternalName(plugin)}");

            try
            {
                using var callbackScope = PluginManager.EnterPluginCallbackScope();
                await plugin.ConfigPromptAsync(PluginManager.Application, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }
}
