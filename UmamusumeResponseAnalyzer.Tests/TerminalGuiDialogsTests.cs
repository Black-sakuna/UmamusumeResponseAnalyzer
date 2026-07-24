using Terminal.Gui.App;
using Terminal.Gui.Input;
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
