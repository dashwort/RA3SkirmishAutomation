namespace RA3SkirmishLauncher.Core.Backend
{
    public sealed class BackendRequest
    {
        public BackendRequest(string command, IReadOnlyDictionary<string, string?> options, string? action = null)
        {
            Command = command;
            Options = options;
            Action = action;
        }

        public string Command { get; }
        public string? Action { get; }
        public IReadOnlyDictionary<string, string?> Options { get; }

        public IReadOnlyList<string> Arguments()
        {
            var result = new List<string> { Command };
            if (Action != null) { result.Add(Action); }
            foreach (var option in Options)
            {
                result.Add("--" + option.Key);
                if (option.Value != null) { result.Add(option.Value); }
            }
            return result;
        }
    }
}
