namespace Urchin.Services;

/// <summary>
/// Scoped (per circuit) event bus for cross-component state notifications.
/// </summary>
public class AppState
{
    /// <summary>Fired when the conversations list should be reloaded (e.g. title generated).</summary>
    public event Action? OnConversationsChanged;

    public void NotifyConversationsChanged() => OnConversationsChanged?.Invoke();
}
