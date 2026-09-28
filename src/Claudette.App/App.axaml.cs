using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core;
using Claudette.Core.Processes;

namespace Claudette.App;

public partial class App : Application
{
    private AppServices? _services;
    private MainWindowViewModel? _mainViewModel;
    private bool _shutdownComplete;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            _services = new AppServices(
                AppPaths.ForCurrentUser(),
                new ProcessLauncher(),
                TimeProvider.System,
                new AvaloniaPlatformServices(() => TopLevel.GetTopLevel(window)),
                new AvaloniaUiDispatcher());
            _mainViewModel = new MainWindowViewModel(_services, FolderArgument(desktop.Args ?? []));
            window.DataContext = _mainViewModel;
            window.Closing += OnMainWindowClosing;
            desktop.MainWindow = window;
            _ = _mainViewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary><c>--folder &lt;path&gt;</c> opens a session in that folder on startup.</summary>
    private static string? FolderArgument(string[] args)
    {
        var index = Array.IndexOf(args, "--folder");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    /// <summary>Stops every Claude Code process before the window closes (interrupting a working turn first).</summary>
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete || sender is not Window window)
        {
            return;
        }
        e.Cancel = true;
        window.IsEnabled = false;
        try
        {
            if (_mainViewModel is not null)
            {
                await _mainViewModel.DisposeAsync();
            }
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
        }
        finally
        {
            _shutdownComplete = true;
            window.Close();
        }
    }
}
