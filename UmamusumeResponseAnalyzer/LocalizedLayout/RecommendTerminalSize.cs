namespace UmamusumeResponseAnalyzer.LocalizedLayout
{
    public class RecommendTerminalSize
    {
        public static TerminalSize Current
        {
            get => Thread.CurrentThread.CurrentUICulture.Name switch
            {
                "zh-CN" => SimplifiedChinese,
                "ja-JP" => Japanese,
                "en-US" => English,
                _ => English
            };
        }
        public static TerminalSize SimplifiedChinese = new(110, 35);
        public static TerminalSize Japanese = new(135, 35);
        public static TerminalSize English = new(110, 35);
        public class TerminalSize
        {
            public int Width { get; }
            public int Height { get; }
            public TerminalSize(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }
    }
}
