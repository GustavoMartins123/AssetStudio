using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AssetStudio.Avalonia;

internal static class AppWindowNavigation
{
    public static bool TryTransition<TNext>(
        ref int transitionGate,
        Func<TNext> createNextWindow,
        Action<TNext> setMainWindow,
        Action<TNext> showAndActivateNextWindow,
        Action? teardownCurrentWindow,
        Action closeCurrentWindow,
        Action<TNext>? onAfterTransition = null)
        where TNext : class
    {
        ArgumentNullException.ThrowIfNull(createNextWindow);
        ArgumentNullException.ThrowIfNull(setMainWindow);
        ArgumentNullException.ThrowIfNull(showAndActivateNextWindow);
        ArgumentNullException.ThrowIfNull(closeCurrentWindow);

        if (Interlocked.CompareExchange(ref transitionGate, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            var nextWindow = createNextWindow();
            setMainWindow(nextWindow);
            showAndActivateNextWindow(nextWindow);
            teardownCurrentWindow?.Invoke();
            closeCurrentWindow();
            onAfterTransition?.Invoke(nextWindow);
            return true;
        }
        finally
        {
            Volatile.Write(ref transitionGate, 0);
        }
    }
}

public partial class App : Application
{
    private int _windowTransitionGate;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Program.WriteCrashLog("Dispatcher unhandled exception", e.Exception);
            e.Handled = true;
        };

        try
        {
            DecryptionHelper.LoadLocalProfilesAndPlugins();
        }
        catch
        {
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = CreateProjectManagerWindow(initialSelectedProjectId: null, autoRefresh: true);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal bool NavigateToMainWindow(ProjectManagerWindow currentWindow, ProjectLaunchContext? context)
    {
        ArgumentNullException.ThrowIfNull(currentWindow);

        return AppWindowNavigation.TryTransition(
            ref _windowTransitionGate,
            createNextWindow: () => context == null ? new MainWindow() : new MainWindow(context),
            setMainWindow: AssignDesktopMainWindow,
            showAndActivateNextWindow: static nextWindow =>
            {
                nextWindow.Show();
                nextWindow.Activate();
            },
            teardownCurrentWindow: null,
            closeCurrentWindow: currentWindow.Close,
            onAfterTransition: static nextWindow => nextWindow.StartProjectIndexingOnOpen());
    }

    internal bool NavigateToProjectManager(MainWindow currentWindow, string? selectedProjectId = null)
    {
        ArgumentNullException.ThrowIfNull(currentWindow);

        return AppWindowNavigation.TryTransition(
            ref _windowTransitionGate,
            createNextWindow: () => CreateProjectManagerWindow(selectedProjectId, autoRefresh: false),
            setMainWindow: AssignDesktopMainWindow,
            showAndActivateNextWindow: static nextWindow =>
            {
                nextWindow.Show();
                nextWindow.Activate();
            },
            teardownCurrentWindow: currentWindow.TeardownProjectSession,
            closeCurrentWindow: currentWindow.Close,
            onAfterTransition: nextWindow => nextWindow.BeginRefreshProjects(selectedProjectId));
    }

    private void AssignDesktopMainWindow(Window nextWindow)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = nextWindow;
        }
    }

    private static ProjectManagerWindow CreateProjectManagerWindow(string? initialSelectedProjectId, bool autoRefresh)
    {
        return new ProjectManagerWindow(initialSelectedProjectId, autoRefresh);
    }
}
