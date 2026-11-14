using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lux.App.Controls;
using Lux.App.Services;
using Lux.App.ViewModels;
using Lux.App.Views;

namespace Lux.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Program.Options;
            if (options.Gallery)
            {
                desktop.MainWindow = new Window { Title = "Lux — controls", Width = 1440, Height = 900, Content = new GalleryView() };
            }
            else
            {
                var platform = new WindowPlatformServices();
                AppServices services;
                try { services = CompositionRoot.Create(options, platform); }
                catch (Exception e)
                {
                    // show the messages of the library as they are; say plainly that a database is damaged; never show a raw exception
                    string why = e is Lux.Library.LibraryException ? e.Message
                        : e.GetType().Name == "SqliteException" ? "The library file could not be read: it may be damaged, or on a drive that is not connected. "
                          + "Your photos and their edits are not in it (they are kept beside the photos). Start Lux with --library and another file name to begin a new library."
                        : Lux.App.ViewModels.Text.Plain(e);
                    desktop.MainWindow = MessageWindow("Lux could not open its library", why);
                    base.OnFrameworkInitializationCompleted();
                    return;
                }
                ThumbnailView.Cache = services.Thumbnails;
                var vm = new MainViewModel(services);
                var window = new MainWindow { DataContext = vm };
                platform.Window = window;
                desktop.MainWindow = window;
                desktop.ShutdownRequested += async (_, _) => await vm.DisposeAsync();
                _ = InitializeAsync(vm, options);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    static async Task InitializeAsync(MainViewModel vm, AppOptions options)
    {
        await vm.InitializeAsync();
        if (options.AddFolder is { Length: > 0 } add)
        {
            try { await Task.Run(() => vm.Services.Library.AddFolderAsync(add)); await vm.RefreshStatsAsync(); await vm.Library.RefreshTreeAsync(); }
            catch (Exception e) { vm.Notify(Lux.App.ViewModels.Text.Plain(e)); }
        }
    }

    static Window MessageWindow(string title, string text) => new()
    {
        Title = "Lux", Width = 640, Height = 240,
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, Classes = { "heading" } },
                new TextBlock { Text = text, Classes = { "wrap", "secondary" } },
            },
        },
    };
}
