namespace Pascension.Net
{
    /// <summary>
    /// Contract between the net layer and the UI bootstrap:
    /// - Networked play (host or client) sets <see cref="Current"/> BEFORE the Game
    ///   scene's Start() callbacks run (HostMatchStarter is created from the
    ///   sceneLoaded hook, which fires after Awake but before Start).
    /// - The UI's GameBootstrap must prefer SessionProvider.Current when it is
    ///   non-null; only when it is null should it build the solo host + LocalSession.
    /// - Solo play never touches this class, so it stays null on that path.
    /// </summary>
    public static class SessionProvider
    {
        /// <summary>The session the UI should render from, or null for solo self-setup.</summary>
        public static ISession Current;

        public static void Clear() => Current = null;
    }
}
