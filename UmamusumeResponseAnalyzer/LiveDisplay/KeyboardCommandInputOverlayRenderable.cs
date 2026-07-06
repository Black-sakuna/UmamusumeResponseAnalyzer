using Spectre.Console;
using Spectre.Console.Rendering;

namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    sealed class KeyboardCommandInputOverlayRenderable(
        IRenderable content,
        KeyboardCommandInput input,
        int width,
        int height) : PopupOverlayRenderable(
            content,
            options => BuildLines(input, width, options.Height ?? height),
            Math.Max(0, width))
    {
        protected override bool TryGetPlacement(int maxWidth, int? maxHeight, int overlayHeight, out OverlayPlacement placement)
        {
            var availableHeight = maxHeight ?? height;
            if (availableHeight <= 0 || overlayHeight <= 0)
            {
                placement = default;
                return false;
            }

            placement = new OverlayPlacement(0, availableHeight - overlayHeight);
            return true;
        }

        protected override int? GetFallbackHeight(int maxWidth, int overlayHeight, OverlayPlacement placement)
            => height > 0 ? height : null;

        static IReadOnlyList<IReadOnlyList<Segment>> BuildLines(KeyboardCommandInput input, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return [];

            if (width < 24 || height < 3)
                return [BuildInputLine(input, width)];

            var lines = new List<IReadOnlyList<Segment>>();
            lines.Add(PlainLine(PopupFrame.TopWithCenteredLabel(width, " Command Mode ")));

            var maxCandidateLines = Math.Max(0, height - 3);
            if (maxCandidateLines > 0 && input.CompletionCandidates.Count > 0)
            {
                var candidateCount = Math.Min(input.CompletionCandidates.Count, maxCandidateLines);
                var hiddenCount = input.CompletionCandidates.Count - candidateCount;
                if (hiddenCount > 0 && candidateCount > 0)
                {
                    candidateCount--;
                    hiddenCount = input.CompletionCandidates.Count - candidateCount;
                }

                for (var i = 0; i < candidateCount; i++)
                    lines.Add(BuildCandidateLine(input.CompletionCandidates[i], width));

                if (hiddenCount > 0)
                    lines.Add(BuildCandidateLine($"+{hiddenCount} more", width));
            }

            lines.Add(BuildFramedInputLine(input, width));
            lines.Add(PlainLine(PopupFrame.BottomWithRightLabel(width, "Tab 补全 · ↑↓ 历史 · Esc 取消")));
            return lines;
        }

        static IReadOnlyList<Segment> BuildCandidateLine(string candidate, int width)
        {
            const string prefix = "│  ";
            const string suffix = " │";
            var contentWidth = Math.Max(0, width - prefix.GetCellWidth() - suffix.GetCellWidth());
            var text = CellText.TrimToCellWidth(candidate, contentWidth);
            var used = prefix.GetCellWidth() + text.GetCellWidth();
            var segments = new List<Segment>
            {
                new(prefix, new Style(foreground: Color.Grey35)),
                new(text, new Style(foreground: Color.Grey35))
            };
            if (used < width - suffix.GetCellWidth())
                segments.Add(Segment.Padding(width - suffix.GetCellWidth() - used));
            segments.Add(new Segment(suffix, new Style(foreground: Color.Grey35)));
            return segments;
        }

        static IReadOnlyList<Segment> BuildFramedInputLine(KeyboardCommandInput input, int width)
        {
            const string left = "│ ";
            const string prompt = "❯ ";
            const string right = " │";
            var inputWidth = Math.Max(0, width - left.GetCellWidth() - prompt.GetCellWidth() - right.GetCellWidth());
            var inputText = CellText.TrimToCellWidth(input.Text, inputWidth);
            var used = left.GetCellWidth() + prompt.GetCellWidth() + inputText.GetCellWidth();
            var segments = new List<Segment>
            {
                new(left, new Style(foreground: Color.Grey35)),
                new(prompt, new Style(foreground: Color.Green)),
                new(inputText, new Style(foreground: Color.White))
            };
            if (used < width - right.GetCellWidth())
                segments.Add(Segment.Padding(width - right.GetCellWidth() - used));
            segments.Add(new Segment(right, new Style(foreground: Color.Grey35)));
            return segments;
        }

        static IReadOnlyList<Segment> BuildInputLine(KeyboardCommandInput input, int width)
        {
            if (width <= 0)
                return [];

            const string prefix = "> ";
            const string hint = "ESC 取消";
            var prefixWidth = prefix.GetCellWidth();
            var hintWidth = hint.GetCellWidth();
            var showHint = width >= prefixWidth + hintWidth + 3;
            var inputWidth = Math.Max(0, width - prefixWidth - (showHint ? hintWidth + 1 : 0));
            var inputText = CellText.TrimToCellWidth(input.Text, inputWidth);
            var used = prefixWidth + inputText.GetCellWidth();
            var segments = new List<Segment>
            {
                new(prefix, new Style(foreground: Color.Green)),
                new(inputText, new Style(foreground: Color.White))
            };

            if (showHint)
            {
                var padding = Math.Max(1, width - used - hintWidth);
                segments.Add(Segment.Padding(padding));
                segments.Add(new Segment(hint, new Style(foreground: Color.Grey35)));
            }
            else if (used < width)
            {
                segments.Add(Segment.Padding(width - used));
            }

            return segments;
        }
    }
}
