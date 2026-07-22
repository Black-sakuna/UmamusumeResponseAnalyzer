using Terminal.Gui.Text;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

static class CellText
{
    public static string FitToCellWidth(string value, int width)
    {
        if (width <= 0)
            return string.Empty;

        var result = new System.Text.StringBuilder(value.Length);
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var columns = rune.GetColumns();
            if (used + columns > width)
                break;
            result.Append(rune);
            used += columns;
        }
        if (used < width)
            result.Append(' ', width - used);
        return result.ToString();
    }
}
