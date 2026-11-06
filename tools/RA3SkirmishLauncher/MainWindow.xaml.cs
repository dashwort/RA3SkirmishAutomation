using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RA3SkirmishLauncher.Core.Backend;
using RA3SkirmishLauncher.Core.Mods;
using RA3SkirmishLauncher.Core.Presentation;
using RA3SkirmishLauncher.Core.Presets;
using RA3SkirmishLauncher.Core.Settings;

namespace RA3SkirmishLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly LaunchViewModel viewModel;
        private readonly DesktopDialogs dialogs;
        private bool initialized;

        public MainWindow()
        {
            InitializeComponent();
            var paths = new LauncherPaths(AppContext.BaseDirectory);
            var sage = new SageSupportManager(
                Path.Combine(paths.ApplicationDirectory, "dependencies", "SAGE-Unlocked", "v1.9.5", "RA3"),
                paths.SettingsFile + ".sage-managed.json");
            viewModel = new LaunchViewModel(
                new BackendClient(Path.Combine(paths.ApplicationDirectory, "RA3SkirmishAutomation.exe")),
                new SettingsStore(paths), sage, paths, new ModDiscovery(), new PresetLibrary());
            Root.DataContext = viewModel;
            dialogs = new DesktopDialogs(WinRT.Interop.WindowNative.GetWindowHandle(this));
            ThemePicker.ItemsSource = new[] { "Dark", "Light", "System" };
            AppWindow.Resize(new Windows.Graphics.SizeInt32(880, 850));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }
            AppWindow.Closing += (_, args) =>
            {
                if (!viewModel.IsBusy) { return; }
                args.Cancel = true;
                viewModel.ReportError("Close RA3 first. The launcher must finish restoring your files before it closes.");
            };
            Activated += (_, args) =>
            {
                if (initialized && args.WindowActivationState != WindowActivationState.Deactivated)
                {
                    viewModel.RefreshPresets();
                }
            };
        }

        private async void Root_Loaded(object sender, RoutedEventArgs args)
        {
            if (initialized) { return; }
            await viewModel.InitializeAsync();
            ThemePicker.SelectedItem = viewModel.Settings.Theme;
            ApplyTheme();
            initialized = true;
        }

        private async void Import_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                var source = await dialogs.ChoosePresetAsync();
                if (source != null) { await SavePresetAsync(source, Path.GetFileNameWithoutExtension(source)); }
            }
            catch (Exception error) { viewModel.ReportError(error.Message); }
        }

        private async void SaveAs_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                var source = viewModel.Presets.Selected?.Path;
                if (source != null) { await SavePresetAsync(source, "Saved skirmish"); }
            }
            catch (Exception error) { viewModel.ReportError(error.Message); }
        }

        private async Task SavePresetAsync(string source, string initialName)
        {
            var name = new TextBox { Header = "Preset name", Text = initialName };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "Save preset", Content = name,
                PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = Root.RequestedTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { return; }
            var filename = name.Text.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? name.Text : name.Text + ".ini";
            var exists = viewModel.Presets.Items.Any(preset => preset.Name.Equals(filename, StringComparison.OrdinalIgnoreCase));
            if (exists && !await ConfirmAsync("Replace saved preset?", $"Replace {filename}?")) { return; }
            viewModel.ImportPreset(source, name.Text, exists);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs args)
        {
            try { dialogs.OpenFolder(viewModel.Settings.PresetDirectory); }
            catch (Exception error) { viewModel.ReportError(error.Message); }
        }

        private async void BrowseFolder_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                var path = await dialogs.ChooseFolderAsync();
                if (path == null) { return; }
                switch ((sender as Button)?.Tag as string)
                {
                    case "game": viewModel.Settings.GameRoot = path; break;
                    case "presets": viewModel.Settings.PresetDirectory = path; break;
                    case "backup": viewModel.Settings.BackupDirectory = path; break;
                }
            }
            catch (Exception error) { viewModel.ReportError(error.Message); }
        }

        private async void ApplySettings_Click(object sender, RoutedEventArgs args)
        {
            await viewModel.ApplySettingsAsync();
            ApplyTheme();
        }

        private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (ThemePicker.SelectedItem is not string theme) { return; }
            viewModel.Settings.Theme = theme;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            Root.RequestedTheme = viewModel.Settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "System" => ElementTheme.Default,
                _ => ElementTheme.Dark
            };
        }

        private async void Patch_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                if ((sender as Button)?.Tag is not string action) { return; }
                if (action == "restore" && !await ConfirmAsync("Restore original engine?",
                    "This removes native skirmish support and restores the original memory setting.")) { return; }
                await viewModel.PatchAsync(action);
            }
            catch (Exception error) { viewModel.ReportError(error.Message); }
        }

        private async Task<bool> ConfirmAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = title, Content = message,
                PrimaryButtonText = "Continue", CloseButtonText = "Cancel", RequestedTheme = Root.RequestedTheme
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
    }
}
