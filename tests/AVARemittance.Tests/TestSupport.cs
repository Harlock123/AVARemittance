using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(AVARemittance.Tests.HeadlessApp))]

namespace AVARemittance.Tests;

/// <summary>The headless Avalonia app the window tests run on: one for the whole run, with Skia so frames can be captured.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs <paramref name="action"/> on the headless UI thread.</summary>
    public static void Run(Action action) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly)
            .Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Waits for a task started on the UI thread, letting the UI thread run meanwhile (blocking it would deadlock).</summary>
    public static T Wait<T>(Task<T> task, int seconds = 30)
    {
        for (var i = 0; i < seconds * 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    public static void Wait(Task task, int seconds = 30) => Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default), seconds);

    /// <summary>Lets queued UI work run.</summary>
    public static void Settle(int times = 20)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }
}

/// <summary>Before any test runs, the program's settings folder is a temporary one: no test reads or writes the person's own.</summary>
internal static class SettingsIsolation
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Isolate() =>
        AVARemittance.MainWindow.FolderOverride = Directory.CreateTempSubdirectory("avaremittance-settings-").FullName;
}
