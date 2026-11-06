namespace EpicWar.Automation;

// Local AppData can be redirected by a packaged parent such as Codex. Keep
// native recovery state outside that virtualization so a normal shell sees
// exactly the same backup, journal and ownership paths.
internal static class NativeAutomationStorage
{
    internal static string Root
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("Could not resolve the user profile for native recovery storage.");
            return Path.Combine(profile, ".epicwar-automation");
        }
    }
}
