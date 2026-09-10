using Newtonsoft.Json;

namespace UmamusumeResponseAnalyzer.Plugin
{
    public class PluginInformation
    {
        public string Author { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        // 保留 manifest 的原始版本字符串；Version 用于数字比较。
        [JsonProperty("Version")]
        public string RawVersion { get; set; } = string.Empty;

        // 比较/排序用的强类型版本(从 RawVersion 解析)。setter 保留直接赋 Version 的调用路径(如测试 Info 助手)。
        [JsonIgnore]
        public Version Version
        {
            get => System.Version.Parse(RawVersion);
            set => RawVersion = value.ToString();
        }
        public string[] Dependencies { get; set; } = [];
        public string[] Targets { get; set; } = [];
        public string RepositoryUrl { get; set; } = string.Empty;
        public long LastUpdate { get; set; }

        public string Category { get; set; } = string.Empty;
        public string Homepage { get; set; } = string.Empty;
        [JsonIgnore]
        public string DownloadUrl { get; set; } = string.Empty;
    }
}
