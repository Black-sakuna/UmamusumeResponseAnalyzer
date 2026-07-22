using Microsoft.Win32;
using UmamusumeResponseAnalyzer.LiveDisplay;

namespace UmamusumeResponseAnalyzer
{
    public static class UraCoreHelper
    {
        private const string GameExeName = "umamusume.exe";
        private static readonly Lazy<List<string>> _lazyGamePaths = new(LoadGamePaths, LazyThreadSafetyMode.ExecutionAndPublication);
        private static List<string> _overrideGamePaths = [];

        public static List<string> GamePaths
        {
            get => _overrideGamePaths.Count != 0 ? _overrideGamePaths : _lazyGamePaths.Value;
            [Obsolete("Migrate to caller-owned game path selection instead of setting GamePaths.", false)]
            set => _overrideGamePaths = value;
        }

        /// <summary>
        /// 从一个可能内嵌 umamusume.exe 全路径的注册表值里提取游戏安装目录前缀；不含 exe 名则返回 null。
        /// </summary>
        internal static string? ExtractGamePathPrefix(string candidate)
        {
            var idx = candidate.IndexOf(GameExeName, StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? candidate[..idx] : null;
        }

        private static List<string> LoadGamePaths()
        {
            if (!OperatingSystem.IsWindows())
                return [];

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // MuiCache — value names that are full exe paths
            TryExtractFromValueNames(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
                Registry.CurrentUser,
                paths);

            // Explorer AppSwitched — same structure
            TryExtractFromValueNames(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
                Registry.CurrentUser,
                paths);

            // AppCompatFlags Compatibility Assistant Store — same structure
            TryExtractFromValueNames(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                Registry.CurrentUser,
                paths);

            // GameConfigStore — each child subkey has MatchedExeFullPath value
            TryExtractFromGameConfigStore(paths);

            return [.. paths];
        }

        /// <summary>
        /// Scans a registry key whose value *names* are full exe paths (e.g. MuiCache).
        /// </summary>
        private static void TryExtractFromValueNames(string subKeyPath, RegistryKey hive, HashSet<string> results)
        {
            try
            {
                using var key = hive.OpenSubKey(subKeyPath);
                if (key is null) return;

                foreach (var name in key.GetValueNames())
                {
                    if (ExtractGamePathPrefix(name) is { } prefix)
                        results.Add(prefix);
                }
            }
            catch { }
        }

        private static void TryExtractFromGameConfigStore(HashSet<string> results)
        {
            try
            {
                using var storeKey = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (storeKey is null) return;

                foreach (var subkeyName in storeKey.GetSubKeyNames())
                {
                    try
                    {
                        using var child = storeKey.OpenSubKey(subkeyName);
                        if (child?.GetValue("MatchedExeFullPath") is string path
                            && ExtractGamePathPrefix(path) is { } prefix)
                        {
                            results.Add(prefix);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void EnableDllRedirection()
        {
            Environment.ExitCode = 1;
            using var registry = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true);

            if (registry == null)
            {
                LiveDisplayConsole.WriteLine("打开注册表失败，请手动操作：https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-redirection#optional-configure-the-registry");
                return;
            }

            // Only skip the prompt when the value is already correctly set to 1.
            if (registry.GetValue("DevOverrideEnable") is int current && current == 1)
            {
                LiveDisplayConsole.WriteLine("注册表已启用DLL重定向，将在三秒后自动关闭。没有做任何改动。");
                Environment.ExitCode = 0;
                return;
            }

            var registryCaution =
                @"该行为具有一定风险，将注册表HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\DevOverrideEnable的值改为1。" + "\n" +
                "请仔细阅读https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-redirection 了解其风险后再做决定，我们不对此负责。";

            if (!LiveDisplayConsole.Confirm(registryCaution))
            {
                LiveDisplayConsole.WriteLine("已取消启用DLL重定向。");
                return;
            }

            registry.SetValue("DevOverrideEnable", 1, RegistryValueKind.DWord);
            if (registry.GetValue("DevOverrideEnable") is int value && value == 1)
            {
                LiveDisplayConsole.WriteLine("已启用DLL重定向，请手动重启Windows使其生效。");
                Environment.ExitCode = 0;
            }
            else
            {
                LiveDisplayConsole.WriteLine("注册表启用DLL重定向失败，请手动检查。");
            }
        }
    }
}
