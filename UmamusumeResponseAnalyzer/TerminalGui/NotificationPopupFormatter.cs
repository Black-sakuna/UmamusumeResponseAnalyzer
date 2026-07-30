namespace UmamusumeResponseAnalyzer.TerminalGui
{
    // 右上角 notification popup 的文本行构建 + 倒计时刷新节流。
    //
    // 无可变 state 所有权——notifications/hotkeyPopup 由 UiHost 持有，每次渲染时以参数传入。
    // 仅 lastPopupCountdownSecond 是 formatter 自己的“上次刷新秒”追踪，避免每帧重建秒级倒计时文本。
    internal sealed class NotificationPopupFormatter
    {
        const int MaxPopupNotifications = 4;

        long lastPopupCountdownSecond = -1;

        public static int GetPopupWidth(int windowWidth)
        {
            if (windowWidth < 70)
                return 0;

            return Math.Clamp(windowWidth / 3, 34, 46);
        }

        // 是否需要因倒计时秒数变化而触发一次重绘。notifications/hotkeyPopup 为只读参数。
        public bool ShouldRefreshPopupCountdown(
            IReadOnlyList<UiNotification> notifications,
            HotkeyPopup? hotkeyPopup,
            DateTimeOffset now)
        {
            var hasHotkeyCountdown = hotkeyPopup?.ExpiresAt > now;
            if (notifications.Count == 0 && !hasHotkeyCountdown)
            {
                lastPopupCountdownSecond = -1;
                return false;
            }

            var second = now.ToUnixTimeSeconds();
            if (second == lastPopupCountdownSecond)
                return false;

            lastPopupCountdownSecond = second;
            return true;
        }

        // 构建 notification popup 的所有文本行。labelResolver 把 workspace 转成显示标签。
        public List<string> BuildLines(
            IReadOnlyList<UiNotification> activeNotifications,
            int popupWidth,
            int maxHeight,
            DateTimeOffset now,
            Func<Workspace, string> labelResolver)
        {
            const int CompactHeight = 3;
            var lines = new List<string>();
            var displayed = 0;
            var displayLimit = Math.Min(activeNotifications.Count, MaxPopupNotifications);
            for (var i = 0; i < displayLimit; i++)
            {
                var cardLines = BuildCardLines(
                    activeNotifications[i],
                    popupWidth,
                    now,
                    labelResolver);
                if (lines.Count + cardLines.Count > maxHeight)
                    break;

                lines.AddRange(cardLines);
                displayed++;
            }

            var remaining = activeNotifications.Count - displayed;
            if (remaining > 0 && lines.Count + CompactHeight <= maxHeight)
            {
                lines.AddRange(BuildCompactLines($"还有 {remaining} 条通知", popupWidth));
            }

            return lines;
        }

        static List<string> BuildCardLines(
            UiNotification notification,
            int popupWidth,
            DateTimeOffset now,
            Func<Workspace, string> labelResolver)
        {
            var lines = new List<string>
            {
                PopupFrame.Top(popupWidth),
                BuildContentLine($"{SeverityText(notification.Severity)} {notification.PluginId}", popupWidth)
            };
            lines.AddRange(
                notification.Text
                    .ReplaceLineEndings("\n")
                    .Split('\n')
                    .Select(line => BuildContentLine(line, popupWidth)));
            lines.Add(BuildContentLine(
                notification.Workspace is null ? "全局通知" : labelResolver(notification.Workspace),
                popupWidth));
            lines.Add(PopupFrame.BottomWithRightLabel(
                popupWidth,
                PopupCountdown.Format(notification.ExpiresAt, now)));
            return lines;
        }

        static IEnumerable<string> BuildCompactLines(string text, int popupWidth)
        {
            yield return PopupFrame.Top(popupWidth);
            yield return BuildContentLine(text, popupWidth);
            yield return PopupFrame.Bottom(popupWidth);
        }

        static string BuildContentLine(string text, int popupWidth)
        {
            return "│ " + CellText.FitToCellWidth(text, popupWidth - 4) + " │";
        }

        static string SeverityText(UiSeverity severity) => severity switch
        {
            UiSeverity.Trace => "TRACE",
            UiSeverity.Info => "INFO ",
            UiSeverity.Success => "OK   ",
            UiSeverity.Warning => "WARN ",
            UiSeverity.Error => "ERR  ",
            _ => "INFO "
        };
    }
}
