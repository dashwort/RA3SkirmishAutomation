using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RA3SkirmishLauncher.Core.Presentation
{
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
            field = value;
            Notify(name);
            return true;
        }

        protected void Notify([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
