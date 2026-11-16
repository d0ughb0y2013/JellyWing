namespace MediaBrowser.Common.Updates;

/// <summary>
/// Installs the plugins a new server is set up with.
/// </summary>
public interface IRecommendedPluginInstaller
{
    /// <summary>
    /// Starts installing the recommended plugins in the background.
    /// </summary>
    void QueueInstall();
}
