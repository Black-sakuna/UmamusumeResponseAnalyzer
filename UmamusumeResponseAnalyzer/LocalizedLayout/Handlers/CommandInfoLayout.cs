namespace UmamusumeResponseAnalyzer.LocalizedLayout.Handlers
{
    public class CommandInfoLayout
    {
        public static CommandInfoLayout Current
        {
            get => Thread.CurrentThread.CurrentUICulture.Name switch
            {
                "zh-CN" => SimplifiedChinese,
                "ja-JP" => Japanese,
                "en-US" => English,
                _ => English
            };
        }
        public static CommandInfoLayout SimplifiedChinese = new(17);
        public static CommandInfoLayout Japanese = new(18);
        public static CommandInfoLayout English = new(16);
        public int TrainingCardWidth { get; init; }
        public int MainSectionWidth => TrainingCardWidth * 5 + 10;
        public CommandInfoLayout(int tcw)
        {
            TrainingCardWidth = tcw;
        }
    }
}
