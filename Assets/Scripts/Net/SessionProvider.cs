namespace Pascension.Net
{
    /// <summary>
    /// Contract between the net layer and the UI bootstrap:
    /// - Networked play (host or client) sets <see cref="Current"/> BEFORE the Game
    ///   scene's Start() callbacks run (HostMatchStarter is created from the
    ///   sceneLoaded hook, which fires after Awake but before Start).
    /// - The UI's GameBootstrap binds SessionProvider.Current when it is
    ///   non-null; otherwise it returns to the menu.
    /// </summary>
    public static class SessionProvider
    {
        /// <summary>The session the UI should render from, or null before joining a match.</summary>
        public static ISession Current;

        public static void Clear() => Current = null;
    }
}
