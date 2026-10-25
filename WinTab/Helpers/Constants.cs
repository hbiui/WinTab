namespace WinTab.Helpers;

internal static class Constants
{
    internal const string AppName = "WinTab";
    internal const string MutexId = $"__{AppName}Hook__Mutex";
    internal const string ShowMainWindowEventName = $"__{AppName}Hook__ShowMainWindow";
    internal const string BackgroundLaunchArg = "--background";
    internal const string OpenRecycleBinArg = "--open-recycle-bin";
    internal const string OpenRecycleBinEventName = $"__{AppName}Hook__OpenRecycleBin";
    internal const string SettingsFileName = "settings.json";
    // This fork checks its own releases; the upstream repo must never be consulted, otherwise an
    // upstream release would prompt an update that overwrites all local modifications.
    internal const string RepositoryOwner = "hbiui";
    internal const string RepositoryName = "WinTab";
    internal const string UpdateUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
}
