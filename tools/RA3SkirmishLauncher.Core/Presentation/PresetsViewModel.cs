using System.Collections.ObjectModel;
using RA3SkirmishLauncher.Core.Presets;

namespace RA3SkirmishLauncher.Core.Presentation
{
    public sealed class PresetsViewModel : ObservableModel
    {
        private readonly IPresetLibrary library;
        private readonly SettingsViewModel settings;
        private PresetChoice? selected;

        public PresetsViewModel(IPresetLibrary library, SettingsViewModel settings)
        {
            this.library = library;
            this.settings = settings;
        }

        public ObservableCollection<PresetChoice> Items { get; } = [];
        public PresetChoice? Selected { get => selected; set => Set(ref selected, value); }

        public void Initialize(string? previousPreset, string bundledPreset)
        {
            library.Initialize(settings.PresetDirectory, previousPreset, bundledPreset);
            Refresh(true);
        }

        public void Refresh(bool selectDefault = false)
        {
            var previous = selectDefault ? null : Selected?.Path;
            Items.Clear();
            foreach (var preset in library.List(settings.PresetDirectory)) { Items.Add(preset); }
            Selected = Items.FirstOrDefault(preset => preset.Path.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
        }

        public void Import(string source, string name, bool overwrite)
        {
            var target = library.Import(settings.PresetDirectory, source, name, overwrite);
            Refresh();
            Selected = Items.First(preset => preset.Path.Equals(target, StringComparison.OrdinalIgnoreCase));
        }
    }
}
