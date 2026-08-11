namespace UmamusumeResponseAnalyzer
{
    public enum ScenarioType
    {
        Ura = 1,
        Aoharu = 2,
        GrandLive = 3,
        MakeANewTrack = 4, //巅峰杯
        GrandMasters = 5,
        LArc = 6,
        UAF = 7,
        Cook = 8,
        Mecha = 9,
        Legend = 10,
        Pioneer = 11,
        Onsen = 12,
        Breeders = 13,
        Unknown = int.MaxValue
    }
    public static class Extensions
    {
        public static string AllowMirror(this string? url)
        {
            if (url == null) return string.Empty;
            if (Config.Updater.IsGithubBlocked && !Config.Updater.ForceUseGithubToUpdate)
            {
                url = url.Replace("https://", "https://gh.shuise.dev/");
            }
            return url;
        }
    }
}
