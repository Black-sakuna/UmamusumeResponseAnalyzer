using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.LiveDisplay;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("KeyboardManager")]
public sealed class TerminalGuiDialogsTests
{
    [Fact]
    public async Task OwnerAsyncBridge_DoesNotDependOnAsyncVoidContextContinuation()
    {
        using var terminal = new TerminalGuiTestApp();
        var context = new RejectSecondPostSynchronizationContext();
        TerminalGuiDialogs.BindOwner(terminal.Application, context);
        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = TerminalGuiDialogs.InvokeOnOwnerAsync(
                terminal.Application,
                async () => await release.Task.ConfigureAwait(false));

            release.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, context.PostCount);
        }
        finally
        {
            TerminalGuiDialogs.UnbindOwner(terminal.Application);
        }
    }

    [Fact]
    public async Task Select_AcceptsFocusedChoiceAndEscCancels()
    {
        using var terminal = new TerminalGuiTestApp();
        string? selected = null;
        var accepted = await terminal.StartAsync(() =>
        {
            selected = TerminalGuiDialogs.Select(
                terminal.Application,
                "选择项",
                ["first", "second"]);
            return Task.CompletedTask;
        });

        await terminal.InjectAsync(Key.CursorDown);
        await terminal.InjectAsync(Key.Enter);
        await accepted;
        Assert.Equal("second", selected);

        var cancelled = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.Select(terminal.Application, "取消选择", ["value"]);
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task Select_ListUsesWholeViewHoverWithoutChangingFocusOrSelection()
    {
        using var terminal = new TerminalGuiTestApp();
        var cancelled = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.Select(
                terminal.Application,
                "列表 hover",
                ["first", "second"]);
            return Task.CompletedTask;
        });
        await terminal.WaitForScreenAsync("second");
        var list = await terminal.InvokeAsync(() =>
            terminal.Application.TopRunnableView!.SubViews.OfType<ListView>().Single());
        await terminal.InjectAsync(Key.Tab);
        var focused = await terminal.InvokeAsync(() =>
            terminal.Application.TopRunnableView!.SubViews.OfType<Button>().Single(x => x.HasFocus));
        await terminal.RedrawAsync();
        var point = await terminal.InvokeAsync(() => list.ViewportToScreen(new Point(1, 1)));
        var normal = await terminal.CaptureAttributeAsync(point);
        var highlight = await terminal.InvokeAsync(() => list.GetAttributeForRole(VisualRole.Highlight));
        Assert.NotEqual(normal, highlight);

        await terminal.MoveMouseAsync(point);
        await terminal.WaitForAsync(async () =>
            Equals(highlight, await terminal.CaptureAttributeAsync(point)));

        Assert.Equal(0, await terminal.InvokeAsync(() => list.SelectedItem));
        Assert.False(await terminal.InvokeAsync(() => list.HasFocus));
        Assert.True(await terminal.InvokeAsync(() => focused.HasFocus));
        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task StartupMenu_UsesNativeMenuFocusAndFramebufferHighlight()
    {
        using var terminal = new TerminalGuiTestApp();
        var cancelled = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.StartupMenu(
                terminal.Application,
                "启动菜单",
                ["开始", "设置", "插件仓库"]);
            return Task.CompletedTask;
        });
        await terminal.WaitForScreenAsync("插件仓库");

        var controls = await terminal.InvokeAsync(() =>
        {
            var dialog = terminal.Application.TopRunnableView!;
            var menu = dialog.SubViews.OfType<Menu>().Single();
            var items = menu.SubViews.OfType<MenuItem>().ToArray();
            return (dialog, menu, items);
        });
        Assert.Single(controls.dialog.SubViews.OfType<Label>());
        Assert.Empty(controls.dialog.SubViews.OfType<ListView>());
        Assert.Empty(controls.dialog.SubViews.OfType<Button>());
        Assert.Equal(["开始", "设置", "插件仓库"], controls.items.Select(x => x.Title.ToString()));
        Assert.True(await terminal.InvokeAsync(() => controls.items[0].HasFocus));
        Assert.Same(controls.items[0], await terminal.InvokeAsync(() => controls.menu.SelectedMenuItem));

        var points = await terminal.InvokeAsync(() => (
            First: controls.items[0].CommandView!.ViewportToScreen(Point.Empty),
            Second: controls.items[1].CommandView!.ViewportToScreen(Point.Empty)));
        await terminal.RedrawAsync();
        var firstFocused = await terminal.CaptureAttributeAsync(points.First);
        var secondNormal = await terminal.CaptureAttributeAsync(points.Second);
        Assert.NotEqual(firstFocused, secondNormal);

        await terminal.MoveMouseAsync(points.Second);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => controls.items[1].HasFocus));
        await terminal.RedrawAsync();

        Assert.Same(controls.items[1], await terminal.InvokeAsync(() => controls.menu.SelectedMenuItem));
        Assert.Equal(secondNormal, await terminal.CaptureAttributeAsync(points.First));
        Assert.Equal(firstFocused, await terminal.CaptureAttributeAsync(points.Second));

        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task StartupMenu_ClickActivatesExactlyOnce()
    {
        using var terminal = new TerminalGuiTestApp();
        string? result = null;
        var run = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.StartupMenu(
                terminal.Application,
                "启动菜单",
                ["开始", "设置"]);
            return Task.CompletedTask;
        });
        var second = await terminal.InvokeAsync(() =>
            terminal.Application.TopRunnableView!
                .SubViews.OfType<Menu>().Single()
                .SubViews.OfType<MenuItem>().ElementAt(1));
        var activations = 0;
        await terminal.InvokeAsync(() => second.Activated += (_, _) => activations++);
        var point = await terminal.InvokeAsync(() =>
            second.CommandView!.ViewportToScreen(Point.Empty));

        await terminal.ClickAsync(point);
        await run;

        Assert.Equal("设置", result);
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task StartupMenu_ArrowKeysAndEnterReturnFocusedItem()
    {
        using var terminal = new TerminalGuiTestApp();
        string? result = null;
        var run = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.StartupMenu(
                terminal.Application,
                "启动菜单",
                ["开始", "设置", "插件仓库"]);
            return Task.CompletedTask;
        });
        var items = await terminal.InvokeAsync(() =>
            terminal.Application.TopRunnableView!
                .SubViews.OfType<Menu>().Single()
                .SubViews.OfType<MenuItem>().ToArray());

        await terminal.InjectAsync(Key.CursorDown);
        Assert.True(await terminal.InvokeAsync(() => items[1].HasFocus));
        await terminal.InjectAsync(Key.CursorUp);
        Assert.True(await terminal.InvokeAsync(() => items[0].HasFocus));
        await terminal.InjectAsync(Key.CursorDown);
        await terminal.InjectAsync(Key.Enter);
        await run;

        Assert.Equal("设置", result);
    }

    [Fact]
    public async Task StartupMenu_EscAndCloseCancel()
    {
        using var terminal = new TerminalGuiTestApp();
        var escaped = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.StartupMenu(
                terminal.Application,
                "Esc 取消",
                ["开始"]);
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => escaped);

        var closed = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.StartupMenu(
                terminal.Application,
                "关闭取消",
                ["开始"]);
            return Task.CompletedTask;
        });
        await terminal.InvokeAsync(() => terminal.Application.RequestStop());
        await Assert.ThrowsAsync<OperationCanceledException>(() => closed);
    }

    [Fact]
    public async Task StartupMenu_ResizeKeepsNativeLayoutAndActivation()
    {
        using var terminal = new TerminalGuiTestApp();
        string? result = null;
        var run = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.StartupMenu(
                terminal.Application,
                "启动菜单",
                ["开始", "设置", "插件仓库"]);
            return Task.CompletedTask;
        });
        var menu = await terminal.InvokeAsync(() =>
            terminal.Application.TopRunnableView!.SubViews.OfType<Menu>().Single());
        var initialWidth = await terminal.InvokeAsync(() => menu.Frame.Width);

        await terminal.ResizeAsync(48, 16);
        await terminal.RedrawAsync();
        var resized = await terminal.InvokeAsync(() => (
            DialogWidth: terminal.Application.TopRunnableView!.Viewport.Width,
            MenuWidth: menu.Frame.Width));

        Assert.True(resized.MenuWidth < initialWidth);
        Assert.Equal(resized.DialogWidth, resized.MenuWidth);
        await terminal.InjectAsync(Key.CursorDown);
        await terminal.InjectAsync(Key.Enter);
        await run;
        Assert.Equal("设置", result);
    }

    [Fact]
    public async Task MultiSelect_CancelThrowsAndConfirmReturnsOnlyMarks()
    {
        using var terminal = new TerminalGuiTestApp();
        var empty = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.MultiSelect<string>(
                terminal.Application,
                "空多选",
                []);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<ArgumentException>(() => empty);

        var cancelled = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.MultiSelect(
                terminal.Application,
                "取消多选",
                ["a", "b"],
                selected: ["a"]);
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);

        IReadOnlyList<string>? result = null;
        var accepted = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.MultiSelect(
                terminal.Application,
                "确认多选",
                ["a", "b", "c"],
                selected: ["a", "c"]);
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.Tab);
        await terminal.InjectAsync(Key.Enter);
        await accepted;

        Assert.Equal(["a", "c"], result);
    }

    [Fact]
    public async Task Ask_UsesRealTextFieldAndEscCancels()
    {
        using var terminal = new TerminalGuiTestApp();
        string? result = null;
        var accepted = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.Ask(terminal.Application, "输入", allowEmpty: false);
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.A);
        await terminal.InjectAsync(Key.Tab);
        await terminal.InjectAsync(Key.Enter);
        await accepted;
        Assert.Equal("a", result);

        var cancelled = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.Ask(terminal.Application, "取消输入", value: "unchanged");
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task Ask_TextFieldAndButtonUseNativeHoverWithoutMovingFocus()
    {
        using var terminal = new TerminalGuiTestApp();
        var cancelled = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.Ask(
                terminal.Application,
                "输入 hover",
                value: "hover-input");
            return Task.CompletedTask;
        });
        await terminal.WaitForScreenAsync("hover-input");
        var controls = await terminal.InvokeAsync(() =>
        {
            var root = terminal.Application.TopRunnableView!;
            return (
                Input: root.SubViews.OfType<TextField>().Single(),
                Buttons: root.SubViews.OfType<Button>().ToArray());
        });
        await terminal.InjectAsync(Key.Tab);
        var focused = controls.Buttons.Single(x => x.HasFocus);
        var otherButton = controls.Buttons.Single(x => !x.HasFocus);
        await terminal.RedrawAsync();

        var inputPoint = await terminal.InvokeAsync(() =>
            controls.Input.ViewportToScreen(new Point(1, 0)));
        var inputNormal = await terminal.CaptureAttributeAsync(inputPoint);
        var inputHighlight = await terminal.InvokeAsync(() =>
            controls.Input.GetAttributeForRole(VisualRole.Highlight));
        Assert.NotEqual(inputNormal, inputHighlight);
        await terminal.MoveMouseAsync(inputPoint);
        await terminal.WaitForAsync(async () =>
            Equals(inputHighlight, await terminal.CaptureAttributeAsync(inputPoint)));
        Assert.True(await terminal.InvokeAsync(() => focused.HasFocus));

        var buttonPoint = await terminal.InvokeAsync(() =>
            otherButton.ViewportToScreen(new Point(1, 0)));
        var buttonNormal = await terminal.CaptureAttributeAsync(buttonPoint);
        var buttonHighlight = await terminal.InvokeAsync(() =>
            otherButton.GetAttributeForRole(VisualRole.Highlight));
        Assert.NotEqual(buttonNormal, buttonHighlight);
        await terminal.MoveMouseAsync(buttonPoint);
        await terminal.WaitForAsync(async () =>
            Equals(buttonHighlight, await terminal.CaptureAttributeAsync(buttonPoint)));
        Assert.True(await terminal.InvokeAsync(() => focused.HasFocus));

        await terminal.InjectAsync(Key.Esc);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirm_EscAlwaysReturnsFalse(bool defaultValue)
    {
        using var terminal = new TerminalGuiTestApp();
        var result = true;
        var run = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.Confirm(
                terminal.Application,
                "危险操作",
                defaultValue);
            return Task.CompletedTask;
        });

        await terminal.InjectAsync(Key.Esc);
        await run;

        Assert.False(result);
    }

    [Fact]
    public async Task Confirm_DefaultFocusIsNo()
    {
        using var terminal = new TerminalGuiTestApp();
        var result = true;
        var run = await terminal.StartAsync(() =>
        {
            result = TerminalGuiDialogs.Confirm(terminal.Application, "危险操作");
            return Task.CompletedTask;
        });

        await terminal.InjectAsync(Key.Enter);
        await run;

        Assert.False(result);
    }

    [Fact]
    public async Task WindowClose_CancelsSelectAndNeverApprovesConfirm()
    {
        using var terminal = new TerminalGuiTestApp();
        var select = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.Select(terminal.Application, "关闭选择", ["value"]);
            return Task.CompletedTask;
        });
        await terminal.InvokeAsync(terminal.Application.RequestStop);
        await Assert.ThrowsAsync<OperationCanceledException>(() => select);

        var confirmed = true;
        var confirm = await terminal.StartAsync(() =>
        {
            confirmed = TerminalGuiDialogs.Confirm(
                terminal.Application,
                "关闭确认",
                defaultValue: true);
            return Task.CompletedTask;
        });
        await terminal.InvokeAsync(terminal.Application.RequestStop);
        await confirm;

        Assert.False(confirmed);
    }

    [Fact]
    public async Task ExternalCancellation_ClosesDialogAndPropagates()
    {
        using var terminal = new TerminalGuiTestApp();
        using var cancellation = new CancellationTokenSource();
        var run = await terminal.StartAsync(() =>
        {
            TerminalGuiDialogs.Select(
                terminal.Application,
                "等待取消",
                ["value"],
                cancellationToken: cancellation.Token);
            return Task.CompletedTask;
        });

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(terminal.Application.SessionStack!);
    }

    [Fact]
    public async Task ExternalCancellation_AlsoClosesAskAndMultiSelect()
    {
        using var terminal = new TerminalGuiTestApp();

        using (var askCancellation = new CancellationTokenSource())
        {
            var ask = await terminal.StartAsync(() =>
            {
                TerminalGuiDialogs.Ask(
                    terminal.Application,
                    "取消输入",
                    cancellationToken: askCancellation.Token);
                return Task.CompletedTask;
            });
            askCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        }

        using (var multiCancellation = new CancellationTokenSource())
        {
            var multi = await terminal.StartAsync(() =>
            {
                TerminalGuiDialogs.MultiSelect(
                    terminal.Application,
                    "取消多选",
                    ["a", "b"],
                    cancellationToken: multiCancellation.Token);
                return Task.CompletedTask;
            });
            multiCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => multi);
        }

        Assert.Empty(terminal.Application.SessionStack!);
    }

    [Fact]
    public async Task Progress_RendersIndependentRowsBeforeCompletion()
    {
        using var terminal = new TerminalGuiTestApp(width: 80, height: 20);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = await terminal.StartAsync(() =>
            TerminalGuiDialogs.RunProgressAsync(
                terminal.Application,
                async (progress, cancellationToken) =>
                {
                    progress.Report(new("first", "Download A", 25, 100));
                    progress.Report(new("second", "Download B", 75, 100));
                    await release.Task.WaitAsync(cancellationToken);
                }));

        try
        {
            await terminal.WaitForScreenAsync("Download A");
            await terminal.WaitForScreenAsync("Download B");
            var screen = await terminal.CaptureScreenAsync();
            Assert.Contains("Download A", screen);
            Assert.Contains("Download B", screen);
            var rows = screen.ReplaceLineEndings("\n").Split('\n');
            Assert.NotEqual(
                Array.FindIndex(rows, row => row.Contains("Download A", StringComparison.Ordinal)),
                Array.FindIndex(rows, row => row.Contains("Download B", StringComparison.Ordinal)));
        }
        finally
        {
            release.TrySetResult();
        }

        await run;
    }

    [Fact]
    public async Task Progress_PropagatesActionFaultAndCancellation()
    {
        using var terminal = new TerminalGuiTestApp();
        var succeeded = await terminal.StartAsync(() =>
            TerminalGuiDialogs.RunProgressAsync(
                terminal.Application,
                (_, _) => Task.CompletedTask));
        await succeeded;

        var faulted = await terminal.StartAsync(() =>
            TerminalGuiDialogs.RunProgressAsync(
                terminal.Application,
                (_, _) => throw new InvalidOperationException("progress failed")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
        Assert.Equal("progress failed", error.Message);

        using var cancellation = new CancellationTokenSource();
        var cancelled = await terminal.StartAsync(() =>
            TerminalGuiDialogs.RunProgressAsync(
                terminal.Application,
                (_, token) => Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token),
                cancellation.Token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var immediateCancellation = await terminal.StartAsync(() =>
            TerminalGuiDialogs.RunProgressAsync(
                terminal.Application,
                (_, token) => Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token),
                alreadyCancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => immediateCancellation);
    }

    [Fact]
    public async Task Progress_PropagatesMainLoopFailureWithoutMasqueradingAsCancellation()
    {
        using var terminal = new TerminalGuiTestApp();
        var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = await terminal.StartAsync(() =>
            TerminalGuiDialogs.RunProgressAsync(
                terminal.Application,
                async (_, cancellationToken) =>
                {
                    actionStarted.SetResult();
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                }));
        await actionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await terminal.InvokeAsync(() => terminal.Application.Iteration += Crash);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.Equal("driver failed", error.Message);
        Assert.Empty(terminal.Application.SessionStack!);

        void Crash(object? sender, Terminal.Gui.App.EventArgs<IApplication?> e)
        {
            terminal.Application.Iteration -= Crash;
            throw new InvalidOperationException("driver failed");
        }
    }

    sealed class RejectSecondPostSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (++PostCount != 1)
                throw new InvalidOperationException("Owner context 已停止接收 continuation。");

            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
