namespace Pascension.Net
{
    /// <summary>Lifecycle of the local player's account session (see AccountService).</summary>
    public enum AccountState
    {
        /// <summary>No mode chosen yet, or the account session ended (logout/expiry).</summary>
        SignedOut,

        /// <summary>The player explicitly chose to play without an account.</summary>
        Guest,

        /// <summary>An auth operation (boot restore, login, create, switch) is in flight.</summary>
        SigningIn,

        /// <summary>Signed in to Unity Gaming Services with a username/password account.</summary>
        SignedIn,

        /// <summary>An account is active locally but UGS could not be reached (no network).</summary>
        SignedInOffline
    }
}
