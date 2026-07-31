using System.Collections.Concurrent;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Tests;

internal sealed class TerminalGuiTestApp : IDisposable
{
    readonly BlockingCollection<RunRequest> runs = [];
    readonly TaskCompletionSource<IApplication> applicationReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Thread uiThread;
    readonly int initialWidth;
    readonly int initialHeight;
    IApplication? ownedApplication;
    int disposed;

    public TerminalGuiTestApp(int width = 80, int height = 24)
    {
        initialWidth = width;
        initialHeight = height;
        Time = new VirtualTimeProvider();
        uiThread = new Thread(RunUiThread) { IsBackground = true, Name = "Terminal.Gui test" };
        uiThread.Start();
        try
        {
            Application = applicationReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            Interlocked.Exchange(ref disposed, 1);
            StopUiThread();
            throw;
        }
    }

    public IApplication Application { get; }

    public VirtualTimeProvider Time { get; }

    public void RunOnOwnerThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Add(new RunRequest(
            () =>
            {
                action();
                return Task.CompletedTask;
            },
            completion));
        completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    public async Task<Task> StartAsync(UiHost host)
        => await StartAsync(host.RunAsync);

    public async Task<Task> StartAsync(Func<Task> run)
    {
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runs.Add(new(run, completion));
            await WaitForAsync(() => Application.TopRunnableView is not null || completion.Task.IsCompleted);
            if (completion.Task.IsCompleted)
            {
                await completion.Task;
                return completion.Task;
            }

            var runnable = Application.TopRunnableView!;
            var screenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Application.Invoke(() =>
            {
                try
                {
                    if (completion.Task.IsCompleted ||
                        !ReferenceEquals(Application.TopRunnableView, runnable))
                    {
                        screenReady.TrySetResult();
                        return;
                    }

                    var driver = Application.Driver
                        ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized.");
                    driver.SetScreenSize(initialWidth, initialHeight);
                    Application.LayoutAndDraw(forceRedraw: true);
                    if (driver.Screen.Width != initialWidth ||
                        driver.Screen.Height != initialHeight ||
                        runnable.NeedsLayout)
                    {
                        throw new InvalidOperationException("Terminal.Gui test screen did not stabilize.");
                    }

                    screenReady.SetResult();
                }
                catch (Exception ex)
                {
                    screenReady.SetException(ex);
                }
            });

            if (await Task.WhenAny(screenReady.Task, completion.Task) == screenReady.Task)
                await screenReady.Task;
            if (completion.Task.IsCompleted)
                await completion.Task;
            return completion.Task;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Task InjectAsync(Key key) => InvokeAsync(() => Application.InjectKey(key));

    public Task InjectAsync(Mouse mouse) => InvokeAsync(() => Application.InjectMouse(mouse));

    public Task MoveMouseAsync(Point point) => InjectAsync(new Mouse
    {
        ScreenPosition = point,
        Flags = MouseFlags.PositionReport,
        Timestamp = Time.Now
    });

    public Task ClickAsync(Point point)
        => InvokeAsync(() => Application.InjectSequence(InputInjectionExtensions.LeftButtonClick(point)));

    public Task ResizeAsync(int width, int height)
        => InvokeAsync(() =>
            (Application.Driver ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized."))
            .SetScreenSize(width, height));

    public Task RedrawAsync()
        => InvokeAsync(() => Application.LayoutAndDraw(forceRedraw: true));

    public async Task<string> CaptureScreenAsync()
    {
        var screen = string.Empty;
        await InvokeAsync(() =>
            screen = (Application.Driver ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized."))
                .ToString());
        return screen;
    }

    public Task<Terminal.Gui.Drawing.Attribute?> CaptureAttributeAsync(Point point)
        => InvokeAsync<Terminal.Gui.Drawing.Attribute?>(() =>
        {
            var contents = (Application.Driver
                ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized."))
                .Contents
                ?? throw new InvalidOperationException("Terminal.Gui framebuffer was not initialized.");
            return contents[point.Y, point.X].Attribute;
        });

    public async Task WaitForScreenAsync(string text)
        => await WaitForAsync(async () =>
            (await CaptureScreenAsync()).Contains(text, StringComparison.Ordinal));

    public async Task StopAsync(UiHost host, Task run)
    {
        host.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            StopAllSessions();
        }
        finally
        {
            StopUiThread();
        }
    }

    void StopUiThread()
    {
        runs.CompleteAdding();
        if (!uiThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Terminal.Gui test thread did not stop.");
        runs.Dispose();
    }

    public async Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Invoke(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        T result = default!;
        await InvokeAsync(() =>
        {
            result = action();
        });
        return result;
    }

    public async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    public async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
            await Task.Delay(10, cancellation.Token);
    }

    void RunUiThread()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            var application = Terminal.Gui.App.Application.Create(Time);
            ownedApplication = application;
            application.Init(DriverRegistry.Names.ANSI);
            (application.Driver ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized."))
                .SetScreenSize(initialWidth, initialHeight);
            SynchronizationContext.SetSynchronizationContext(new OwnerSynchronizationContext(this));
            application.Iteration += ApplicationIteration;
            applicationReady.SetResult(application);
            foreach (var request in runs.GetConsumingEnumerable())
                request.Execute();
        }
        catch (Exception ex)
        {
            applicationReady.TrySetException(ex);
            while (runs.TryTake(out var request))
                request.Completion.TrySetException(ex);
        }
        finally
        {
            if (ownedApplication is { } application)
                application.Iteration -= ApplicationIteration;
            ownedApplication?.Dispose();
            ownedApplication = null;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    Task DispatchToOwner(SendOrPostCallback callback, object? state)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new RunRequest(
            () =>
            {
                callback(state);
                return Task.CompletedTask;
            },
            completion);
        runs.Add(request);

        var application = Volatile.Read(ref ownedApplication);
        if (application is not null)
        {
            try
            {
                application.Invoke(static () => { });
            }
            catch (NotInitializedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return completion.Task;
    }

    void ApplicationIteration(
        object? sender,
        Terminal.Gui.App.EventArgs<IApplication?> e)
    {
        while (runs.TryTake(out var request))
            request.Execute();
    }

    void StopAllSessions()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Application.TopRunnableView is { } runnable)
        {
            try
            {
                InvokeAsync(Application.RequestStop).GetAwaiter().GetResult();
            }
            catch when (DateTime.UtcNow < deadline)
            {
            }

            while (ReferenceEquals(Application.TopRunnableView, runnable) && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Terminal.Gui test session did not stop.");
        }
    }

    sealed class OwnerSynchronizationContext(TerminalGuiTestApp owner) : SynchronizationContext
    {
        readonly int ownerThreadId = Environment.CurrentManagedThreadId;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _ = owner.DispatchToOwner(callback, state);
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (Environment.CurrentManagedThreadId == ownerThreadId)
            {
                callback(state);
                return;
            }

            owner.DispatchToOwner(callback, state).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    sealed class RunRequest(
        Func<Task> run,
        TaskCompletionSource completion)
    {
        int started;

        public TaskCompletionSource Completion { get; } = completion;

        public void Execute()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            try
            {
                var task = run();
                if (task.IsCompleted)
                {
                    Complete(task);
                    return;
                }

                _ = task.ContinueWith(
                    static (completed, state) => ((RunRequest)state!).Complete(completed),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Completion.SetException(ex);
            }
        }

        void Complete(Task task)
        {
            try
            {
                task.GetAwaiter().GetResult();
                Completion.SetResult();
            }
            catch (Exception ex)
            {
                Completion.SetException(ex);
            }
        }
    }
}
