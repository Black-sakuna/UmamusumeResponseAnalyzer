using Newtonsoft.Json;
using System.Globalization;
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
    }

    public class RepositoryConfig
    {
        public List<string> Targets { get; set; } = [];
    }

    public class PluginConfig
    {
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
    }

    public class LanguageConfig
    {
        public Language Selected { get; internal set; } = Language.AutoDetect;

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
