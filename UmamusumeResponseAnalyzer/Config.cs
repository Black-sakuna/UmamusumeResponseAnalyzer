using Newtonsoft.Json;
using System.Globalization;
using System.Net;
using System.Reflection;
using UmamusumeResponseAnalyzer.LiveDisplay;
using UmamusumeResponseAnalyzer.Plugin;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using i18n = UmamusumeResponseAnalyzer.Localization.Config;

namespace UmamusumeResponseAnalyzer
{
    public static class Config
    {
        internal static string CONFIG_FILEPATH = "config.yaml";
        private static YamlConfig Current { get; set; }
        private readonly static ISerializer _serializer = new SerializerBuilder().WithQuotingNecessaryStrings().WithNamingConvention(HyphenatedNamingConvention.Instance).Build();
        private readonly static IDeserializer _deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().WithNamingConvention(HyphenatedNamingConvention.Instance).Build();
        public static CoreConfig Core => Current.Core;
        public static RepositoryConfig Repository => Current.Repository;
        public static PluginConfig Plugin => Current.Plugin;
        public static UpdaterConfig Updater => Current.Updater;
        public static LanguageConfig Language => Current.Language;
        public static MiscConfig Misc => Current.Misc;

        internal static void Initialize()
        {
            if (File.Exists(CONFIG_FILEPATH))
            {
                var config = _deserializer.Deserialize<YamlConfig>(File.ReadAllText(CONFIG_FILEPATH))
                    ?? throw new InvalidDataException($"配置文件“{CONFIG_FILEPATH}”内容为 null，请修复后重试。");
                var nullSection = config switch
                {
                    { Core: null } => nameof(YamlConfig.Core),
                    { Repository: null } => nameof(YamlConfig.Repository),
                    { Plugin: null } => nameof(YamlConfig.Plugin),
                    { Updater: null } => nameof(YamlConfig.Updater),
                    { Language: null } => nameof(YamlConfig.Language),
                    { Misc: null } => nameof(YamlConfig.Misc),
                    _ => null
                };
                if (nullSection is not null)
                    throw new InvalidDataException($"配置文件“{CONFIG_FILEPATH}”的 {nullSection} section 为 null，请修复后重试。");

                Current = config;
                UmamusumeResponseAnalyzer.ApplyCultureInfo();
            }
            else
            {
                Current = new();
                Save();
                // 首次运行也要应用 culture,否则首启菜单会用 OS 区域(如繁中系统→无对应资源→回退英文)。
                UmamusumeResponseAnalyzer.ApplyCultureInfo();
            }
        }

        public static void Save() =>
            File.WriteAllText(CONFIG_FILEPATH, _serializer.Serialize(Current));

        internal static async Task PromptAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    var selected = LiveDisplayConsole.Menu(
                        i18n.Settings_Title,
                        new[]
                        {
                            i18n.Tabs_Core_Title,
                            i18n.Tabs_Repository_Title,
                            i18n.Tabs_Plugin_Title,
                            i18n.Tabs_Updater_Title,
                            i18n.Tabs_Language_Title,
                            i18n.Tabs_Misc_Title,
                            i18n.Return
                        },
                        cancellationToken: cancellationToken);
                    if (selected == i18n.Return)
                        return;

                    if (selected == i18n.Tabs_Core_Title)
                        Core.Prompt(cancellationToken);
                    else if (selected == i18n.Tabs_Repository_Title)
                        Repository.Prompt(cancellationToken);
                    else if (selected == i18n.Tabs_Plugin_Title)
                        await Plugin.PromptAsync(cancellationToken);
                    else if (selected == i18n.Tabs_Updater_Title)
                        Updater.Prompt(cancellationToken);
                    else if (selected == i18n.Tabs_Language_Title)
                        Language.Prompt(cancellationToken);
                    else if (selected == i18n.Tabs_Misc_Title)
                        Misc.Prompt(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

    }

    public class YamlConfig
    {
        public CoreConfig Core { get; set; } = new();
        public RepositoryConfig Repository { get; set; } = new();
        public PluginConfig Plugin { get; set; } = new();
        public UpdaterConfig Updater { get; set; } = new();
        public LanguageConfig Language { get; set; } = new();
        public MiscConfig Misc { get; set; } = new();
    }

    #region class
    public class CoreConfig
    {
        public string ListenAddress { get; set; } = "127.0.0.1";
        public int ListenPort { get; set; } = 4693;
        public bool ShowFirstRunPrompt { get; set; } = true;

        internal void Prompt(CancellationToken cancellationToken)
        {
            var firstRunTitle = i18n.ResourceManager.GetString(
                    "Tabs_Core_ShowFirstRunPrompt",
                    i18n.Culture)
                ?? nameof(ShowFirstRunPrompt);
            while (true)
            {
                var addressItem = $"{i18n.Tabs_Core_ListenAddress}: {ListenAddress}";
                var portItem = $"{i18n.Tabs_Core_ListenPort}: {ListenPort}";
                var firstRunItem = $"{firstRunTitle}: {ShowFirstRunPrompt}";
                var selected = LiveDisplayConsole.Menu(
                    i18n.Tabs_Core_Title,
                    new[] { addressItem, portItem, firstRunItem, i18n.Return },
                    cancellationToken: cancellationToken);
                if (selected == i18n.Return)
                    return;

                if (selected == addressItem)
                {
                    while (true)
                    {
                        var address = LiveDisplayConsole.Ask(
                            i18n.Tabs_Core_ListenAddressPrompt,
                            ListenAddress,
                            cancellationToken: cancellationToken);
                        if (!IPAddress.TryParse(address, out _))
                            continue;
                        ListenAddress = address;
                        break;
                    }
                }
                else if (selected == portItem)
                {
                    while (true)
                    {
                        var port = LiveDisplayConsole.Ask(
                            i18n.Tabs_Core_ListenPortPrompt,
                            ListenPort.ToString(),
                            cancellationToken: cancellationToken);
                        if (!int.TryParse(port, out var parsed))
                            continue;
                        ListenPort = parsed;
                        break;
                    }
                }
                else if (selected == firstRunItem)
                {
                    ShowFirstRunPrompt = !ShowFirstRunPrompt;
                }
                Config.Save();
            }
        }
    }

    public class RepositoryConfig
    {
        public List<string> Targets { get; set; } = [];

        internal void Prompt(CancellationToken cancellationToken)
        {
            while (true)
            {
                var targetsItem = $"{i18n.Tabs_Repository_Targets}: {string.Join(',', Targets)}";
                var selected = LiveDisplayConsole.Menu(
                    i18n.Tabs_Repository_Title,
                    new[] { targetsItem, i18n.Return },
                    cancellationToken: cancellationToken);
                if (selected == i18n.Return)
                    return;

                var input = LiveDisplayConsole.Ask(
                    i18n.Tabs_Repository_TargetsPrompt,
                    string.Join(',', Targets),
                    allowEmpty: true,
                    cancellationToken: cancellationToken);
                Targets = string.IsNullOrEmpty(input)
                    ? []
                    : [.. input.Replace('，', ',').Split(',')];
                Config.Save();
            }
        }
    }

    public class PluginConfig
    {
        internal async Task PromptAsync(CancellationToken cancellationToken)
        {
            var plugins = BuildPluginChoices(PluginManager.SnapshotLoadedPlugins());
            var choices = plugins
                .Select(x => (Label: x.Key, Plugin: (IPlugin?)x.Value))
                .Append((i18n.Return, null))
                .ToArray();
            while (true)
            {
                var selected = LiveDisplayConsole.Menu(
                    i18n.Tabs_Plugin_Title,
                    choices,
                    x => x.Label,
                    cancellationToken: cancellationToken);
                if (selected.Plugin is null)
                    return;
                await PluginConfigPrompt.RunAsync(selected.Plugin, cancellationToken);
            }
        }

        internal static SortedDictionary<string, IPlugin> BuildPluginChoices(IEnumerable<IPlugin> plugins)
        {
            var list = plugins.ToList();
            var duplicateNames = list
                .GroupBy(x => x.Name, StringComparer.Ordinal)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToHashSet(StringComparer.Ordinal);

            var choices = new SortedDictionary<string, IPlugin>(StringComparer.Ordinal);
            foreach (var plugin in list)
            {
                var baseLabel = duplicateNames.Contains(plugin.Name)
                    ? $"{plugin.Name} ({plugin.Author}/{PluginManager.InternalName(plugin)})"
                    : plugin.Name;

                var label = baseLabel;
                for (var suffix = 2; choices.ContainsKey(label); suffix++)
                    label = $"{baseLabel} #{suffix}";

                choices.Add(label, plugin);
            }
            return choices;
        }
    }

    public class UpdaterConfig
    {
        public bool IsGithubBlocked { get; set; } = RegionInfo.CurrentRegion.Name == "CN" || CultureInfo.CurrentUICulture.Name == "zh-CN";
        public bool TrainerIsMale { get; set; } = true;
        public string DatabaseLanguage { get; set; } = "ja-JP";
        public string CustomDatabaseRepository { get; set; }
        public bool ForceUseGithubToUpdate { get; set; }

        internal void Prompt(CancellationToken cancellationToken)
        {
            string Label(string property) =>
                i18n.ResourceManager.GetString($"Tabs_Updater_{property}", i18n.Culture)
                ?? property;

            while (true)
            {
                var trainerItem = $"{Label(nameof(TrainerIsMale))}: {TrainerIsMale}";
                var languageItem = $"{Label(nameof(DatabaseLanguage))}: {DatabaseLanguage}";
                var repositoryItem =
                    $"{Label(nameof(CustomDatabaseRepository))}: {CustomDatabaseRepository}";
                var forceGithubItem =
                    $"{i18n.Tabs_Updater_ForceUseGithubToUpdate}: {ForceUseGithubToUpdate}";
                var selected = LiveDisplayConsole.Menu(
                    i18n.Tabs_Updater_Title,
                    new[]
                    {
                        trainerItem,
                        languageItem,
                        repositoryItem,
                        forceGithubItem,
                        i18n.Return
                    },
                    cancellationToken: cancellationToken);
                if (selected == i18n.Return)
                    return;

                if (selected == trainerItem)
                {
                    TrainerIsMale = !TrainerIsMale;
                }
                else if (selected == languageItem)
                {
                    DatabaseLanguage = LiveDisplayConsole.Menu(
                        nameof(DatabaseLanguage),
                        new[] { "ja-JP", "zh-TW", "zh-CN" },
                        cancellationToken: cancellationToken);
                }
                else if (selected == repositoryItem)
                {
                    while (true)
                    {
                        var url = LiveDisplayConsole.Ask(
                            i18n.Tabs_Updater_CustomDatabaseRepositoryPrompt,
                            CustomDatabaseRepository,
                            allowEmpty: true,
                            cancellationToken: cancellationToken);
                        if (!string.IsNullOrEmpty(url) &&
                            !Uri.TryCreate(url, UriKind.Absolute, out _))
                            continue;
                        CustomDatabaseRepository = url;
                        break;
                    }
                }
                else if (selected == forceGithubItem)
                {
                    ForceUseGithubToUpdate = !ForceUseGithubToUpdate;
                }
                Config.Save();
            }
        }
    }

    public class LanguageConfig
    {
        public Language Selected { get; internal set; } = Language.AutoDetect;

        internal void Prompt(CancellationToken cancellationToken)
        {
            var choices = Enum.GetValues<Language>()
                .ToDictionary(
                    language => i18n.ResourceManager.GetString(
                            $"Tabs_Language_{language}",
                            i18n.Culture)
                        ?? language.ToString());
            var selected = LiveDisplayConsole.Menu(
                i18n.Tabs_Language_Title,
                choices.Keys,
                cancellationToken: cancellationToken);
            Selected = choices[selected];
            Config.Save();
            UmamusumeResponseAnalyzer.Restart();
        }

        public static string GetCulture()
        {
            return Config.Language.Selected switch
            {
                Language.SimplifiedChinese => "zh-CN",
                Language.Japanese => "ja-JP",
                Language.English => "en-US",
                _ => AutoDetectCulture(Thread.CurrentThread.CurrentCulture.Name),
            };
        }

        // AutoDetect:把 OS 区域映射到最接近的「已提供 UI 资源」的语言。
        // 只有 zh-CN/ja-JP/en-US(+invariant 英文)有 .resx;繁中(zh-TW)/zh-HK 等没有对应资源,
        // 未提供资源的 OS 区域名会让 ResourceManager 回退 invariant 英文,导致繁中系统下整个 UI 变英文。
        // 这里把所有 zh-* 归到 zh-CN(目前唯一的中文 UI 资源),其余按语言主标签归类,未知归 en-US。
        internal static string AutoDetectCulture(string osCultureName) =>
            osCultureName.Split('-')[0] switch
            {
                "zh" => "zh-CN",
                "ja" => "ja-JP",
                "en" => "en-US",
                _ => "en-US",
            };

        public enum Language
        {
            AutoDetect,
            SimplifiedChinese,
            Japanese,
            English
        }
    }

    public class MiscConfig
    {
        public bool SaveResponseForDebug { get; set; }
        public void Prompt(CancellationToken cancellationToken)
        {
            var _properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var translated = _properties.Select(x => x.Name).ToDictionary(x => x, x => i18n.ResourceManager.GetString($"Tabs_Debug_{x}", i18n.Culture)!);
            var selected = _properties
                .Where(x => (bool)x.GetValue(this)!)
                .Select(x => translated[x.Name]);
            var l3 = LiveDisplayConsole.MultiSelect(
                i18n.Tabs_Debug_Title,
                translated.Values,
                selected,
                cancellationToken: cancellationToken);
            foreach (var i in _properties)
            {
                i.SetValue(this, l3.Contains(translated[i.Name]));
            }
            Config.Save();
        }
    }
    #endregion
}
