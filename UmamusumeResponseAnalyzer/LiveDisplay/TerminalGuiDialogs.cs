using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

static class TerminalGuiDialogs
{
    public static async Task RunProgressAsync(Func<IProgress<DownloadProgress>, Task> action)
    {
        using IApplication app = Application.Create();
        app.Init();
        using var dialog = CreateDialog("正在处理", height: 7);
        var label = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Text = "准备中…" };
        var bar = new ProgressBar { X = 1, Y = 2, Width = Dim.Fill(1) };
        dialog.Add(label, bar);
        var progress = new Progress<DownloadProgress>(value => app.Invoke(() =>
        {
            label.Text = value.Description;
            bar.Fraction = value.Total <= 0 ? 0 : Math.Clamp((float)value.Completed / value.Total, 0, 1);
        }));
        var task = Task.Run(() => action(progress));
        _ = task.ContinueWith(
            _ => app.Invoke(() => app.RequestStop(dialog)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        app.Run(dialog);
        await task;
    }

    public static T Select<T>(string title, IEnumerable<T> choices, Func<T, string>? converter)
    {
        var values = choices.ToArray();
        if (values.Length == 0)
            throw new ArgumentException("选择列表不能为空。", nameof(choices));

        var index = RunList(title, values.Select(x => converter?.Invoke(x) ?? x?.ToString() ?? string.Empty).ToArray());
        return values[index];
    }

    public static IReadOnlyList<T> MultiSelect<T>(
        string title,
        IEnumerable<T> choices,
        IEnumerable<T>? selected,
        Func<T, string>? converter)
    {
        var values = choices.ToArray();
        if (values.Length == 0)
            return [];

        using IApplication app = Application.Create();
        app.Init();
        using var dialog = CreateDialog(title);
        var list = CreateList(values.Select(x => converter?.Invoke(x) ?? x?.ToString() ?? string.Empty));
        list.MarkMultiple = true;
        list.ShowMarks = true;
        var selectedValues = selected?.ToHashSet() ?? [];
        for (var i = 0; i < values.Length; i++)
        {
            if (selectedValues.Contains(values[i]))
                list.SetSelection(i, true);
        }

        var accepted = false;
        var ok = CreateButton("确定", isDefault: true, () =>
        {
            accepted = true;
            app.RequestStop(dialog);
        });
        var cancel = CreateButton("取消", isDefault: false, () => app.RequestStop(dialog));
        Layout(dialog, list, ok, cancel);
        app.Run(dialog);
        return accepted ? list.GetAllMarkedItems().Select(x => values[x]).ToArray() : [];
    }

    public static string Ask(string title, string? value, bool allowEmpty)
    {
        while (true)
        {
            using IApplication app = Application.Create();
            app.Init();
            using var dialog = CreateDialog(title, height: 7);
            var input = new TextField
            {
                Text = value ?? string.Empty,
                X = 1,
                Y = 1,
                Width = Dim.Fill(1)
            };
            var accepted = false;
            var ok = CreateButton("确定", true, () =>
            {
                accepted = true;
                app.RequestStop(dialog);
            });
            var cancel = CreateButton("取消", false, () => app.RequestStop(dialog));
            ok.X = Pos.Center() - 10;
            ok.Y = Pos.Bottom(input) + 1;
            cancel.X = Pos.Right(ok) + 2;
            cancel.Y = ok.Y;
            dialog.Add(input, ok, cancel);
            input.SetFocus();
            app.Run(dialog);

            var result = input.Text;
            if (!accepted)
                throw new OperationCanceledException("输入已取消。");
            if (allowEmpty || !string.IsNullOrWhiteSpace(result))
                return result;
            value = result;
        }
    }

    public static bool Confirm(string title, bool defaultValue)
    {
        using IApplication app = Application.Create();
        app.Init();
        using var dialog = CreateDialog(title, height: 7);
        var result = defaultValue;
        var yes = CreateButton("是", defaultValue, () =>
        {
            result = true;
            app.RequestStop(dialog);
        });
        var no = CreateButton("否", !defaultValue, () =>
        {
            result = false;
            app.RequestStop(dialog);
        });
        yes.X = Pos.Center() - 8;
        yes.Y = 1;
        no.X = Pos.Right(yes) + 2;
        no.Y = yes.Y;
        dialog.Add(yes, no);
        (defaultValue ? yes : no).SetFocus();
        app.Run(dialog);
        return result;
    }

    static int RunList(string title, IReadOnlyList<string> choices)
    {
        using IApplication app = Application.Create();
        app.Init();
        using var dialog = CreateDialog(title);
        var list = CreateList(choices);
        var accepted = false;
        var ok = CreateButton("确定", true, () =>
        {
            accepted = true;
            app.RequestStop(dialog);
        });
        var cancel = CreateButton("取消", false, () => app.RequestStop(dialog));
        list.Accepted += (_, _) =>
        {
            accepted = true;
            app.RequestStop(dialog);
        };
        Layout(dialog, list, ok, cancel);
        list.SetFocus();
        app.Run(dialog);
        if (!accepted)
            throw new OperationCanceledException("选择已取消。");
        return list.SelectedItem ?? 0;
    }

    static Dialog CreateDialog(string title, int height = 20)
        => new()
        {
            Title = title,
            Width = Dim.Percent(80),
            Height = Math.Min(height, Math.Max(5, Console.WindowHeight - 2))
        };

    static ListView CreateList(IEnumerable<string> choices)
    {
        var list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = true
        };
        list.SetSource(new ObservableCollection<string>(choices));
        list.SelectedItem = 0;
        return list;
    }

    static Button CreateButton(string text, bool isDefault, Action action)
    {
        var button = new Button { Text = text, IsDefault = isDefault };
        button.Accepting += (_, _) => action();
        return button;
    }

    static void Layout(Dialog dialog, ListView list, Button ok, Button cancel)
    {
        ok.X = Pos.Center() - 10;
        ok.Y = Pos.Bottom(list);
        cancel.X = Pos.Right(ok) + 2;
        cancel.Y = ok.Y;
        dialog.Add(list, ok, cancel);
    }
}
