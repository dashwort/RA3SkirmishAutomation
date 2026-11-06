using System.Windows.Input;

namespace RA3SkirmishLauncher.Core.Presentation
{
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> execute;
        private readonly Func<bool> canExecute;
        private bool executing;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !executing && canExecute();
        public async void Execute(object? parameter) => await ExecuteAsync();

        public async Task ExecuteAsync()
        {
            if (!CanExecute(null)) { return; }
            executing = true;
            Refresh();
            try { await execute(); }
            finally { executing = false; Refresh(); }
        }

        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
