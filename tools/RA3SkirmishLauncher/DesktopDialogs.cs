using System.Diagnostics;
using Windows.Storage.Pickers;

namespace RA3SkirmishLauncher
{
    internal sealed class DesktopDialogs
    {
        private readonly nint windowHandle;

        public DesktopDialogs(nint windowHandle)
        {
            this.windowHandle = windowHandle;
        }

        public async Task<string?> ChoosePresetAsync()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".ini");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            return (await picker.PickSingleFileAsync())?.Path;
        }

        public async Task<string?> ChooseFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            return (await picker.PickSingleFolderAsync())?.Path;
        }

        public void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(start);
        }
    }
}
