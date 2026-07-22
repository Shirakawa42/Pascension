using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace Pascension.Net
{
    /// <summary>
    /// The local player's account: Unity Gaming Services username/password sessions,
    /// the guest mode, and the persisted choice between them. This is the ONLY file
    /// that performs UGS authentication (UgsGateway keeps the Relay calls). All state
    /// mutations happen on the Unity main thread; <see cref="Changed"/> fires on every
    /// state or username transition. Every failure surfaces as a user-readable
    /// <see cref="UgsException"/>.
    /// </summary>
    public static class AccountService
    {
        // Editor-salted like ClientIdentity.GuidKey so MPPM virtual players (shared
        // PlayerPrefs store, distinct dataPath) keep independent account choices.
        private const string ModeKeyPrefix = "pascension.account.mode";       // "account" | "guest"
        private const string ActiveKeyPrefix = "pascension.account.active";   // display-casing username
        private const string KnownKeyPrefix = "pascension.account.known";     // '\n'-joined usernames
        private const string PlayerIdKeyPrefix = "pascension.account.playerid."; // + LocalProfileId

        private const string UsernameRuleError = "Usernames are 3-20 characters: letters, digits, . - @ _";
        private const string SessionExpiredNotice = "Your session expired — please sign in again.";

        private static AccountState _state = AccountState.SignedOut;
        private static string _username;   // display casing; only meaningful when signed in
        private static string _playerId;   // live UGS id, or the cached one when offline
        private static string _pendingNotice;
        private static Task _boot;
        private static Task _op;

        // ------------------------------------------------------------------ surface

        public static AccountState State => _state;

        /// <summary>Fires on every state or username transition (main thread).</summary>
        public static event Action Changed;

        /// <summary>Display-casing username; null unless SignedIn/SignedInOffline.</summary>
        public static string CurrentUsername =>
            _state == AccountState.SignedIn || _state == AccountState.SignedInOffline ? _username : null;

        /// <summary>Live UGS player id (or the cached one when offline); null for guests.</summary>
        public static string PlayerId =>
            _state == AccountState.SignedIn || _state == AccountState.SignedInOffline ? _playerId : null;

        /// <summary>Stable local id for per-account data: "guest" or "acct" + fnv32hex(lower(username)).</summary>
        public static string LocalProfileId =>
            CurrentUsername != null ? LocalProfileFor(CurrentUsername) : "guest";

        /// <summary>True until the player has picked account or guest mode once.</summary>
        public static bool FirstRunChoicePending => !PlayerPrefs.HasKey(Salted(ModeKeyPrefix));

        /// <summary>Multiplayer entry gate. Guests may play online in the EDITOR only
        /// (the dev fallback in <see cref="EnsureOnlineAsync"/> keeps MPPM working).</summary>
        public static bool CanPlayOnline
        {
            get
            {
                if (_state == AccountState.SignedIn || _state == AccountState.SignedInOffline) return true;
#if UNITY_EDITOR
                if (_state == AccountState.Guest) return true;
#endif
                return false;
            }
        }

        /// <summary>One-shot boot notice (session expiry discovered while restoring);
        /// reading it consumes it. The account panel shows it on its status line.</summary>
        public static string PendingNotice
        {
            get
            {
                string notice = _pendingNotice;
                _pendingNotice = null;
                return notice;
            }
        }

        /// <summary>Usernames that signed in on this device (display casing, oldest first).</summary>
        public static IReadOnlyList<string> KnownAccounts
        {
            get
            {
                string raw = PlayerPrefs.GetString(Salted(KnownKeyPrefix), "");
                return string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split('\n');
            }
        }

        /// <summary>Null when valid, else the English rule error (localize via Loc.T).</summary>
        public static string ValidateUsername(string username)
        {
            username = (username ?? "").Trim();
            return Regex.IsMatch(username, @"^[A-Za-z0-9.\-@_]{3,20}$") ? null : UsernameRuleError;
        }

        // ------------------------------------------------------------------ boot

        /// <summary>Restore the persisted account choice. Idempotent (the first call's
        /// task is cached); never throws — failures land in a state + PendingNotice.</summary>
        public static Task BootAsync() => _boot ??= BootInternalAsync();

        private static async Task BootInternalAsync()
        {
            string mode = PlayerPrefs.GetString(Salted(ModeKeyPrefix), "");
            if (mode == "guest")
            {
                Debug.Log("[Account] boot: guest mode");
                Set(AccountState.Guest, null);
                return;
            }
            string active = mode == "account" ? PlayerPrefs.GetString(Salted(ActiveKeyPrefix), "") : "";
            if (string.IsNullOrEmpty(active))
            {
                Debug.Log("[Account] boot: signed out (mode '" + mode + "')");
                Set(AccountState.SignedOut, null);
                return;
            }

            Debug.Log("[Account] boot: restoring session for " + active);
            Set(AccountState.SigningIn, null);
            try
            {
                await EnsureProfileAsync(AuthProfileFor(active));
                if (!AuthenticationService.Instance.SessionTokenExists)
                {
                    Debug.LogWarning("[Account] boot: no cached session token for " + active);
                    Set(AccountState.SignedOut, null);
                    return;
                }
                // In Auth 3.x this continues the cached session token — it restores the
                // username/password account the token belongs to, not an anonymous one.
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                CompleteSignIn(active);
            }
            catch (Exception e)
            {
                if (IsAuthRejection(e))
                {
                    Debug.LogWarning("[Account] boot: session rejected for " + active + " — " + e.Message);
                    _pendingNotice = SessionExpiredNotice;
                    Set(AccountState.SignedOut, null);
                }
                else
                {
                    Debug.LogWarning("[Account] boot: offline — using cached identity for " + active + " (" + e.Message + ")");
                    _playerId = EmptyToNull(PlayerPrefs.GetString(Salted(PlayerIdKeyPrefix + LocalProfileFor(active)), ""));
                    ClientIdentity.PlayerName = active;
                    Set(AccountState.SignedInOffline, active);
                }
            }
        }

        // ------------------------------------------------------------------ account ops

        public static Task CreateAccountAsync(string username, string rawPassword) =>
            AuthOpAsync(username, rawPassword, signUp: true);

        public static Task LoginAsync(string username, string rawPassword) =>
            AuthOpAsync(username, rawPassword, signUp: false);

        private static Task AuthOpAsync(string username, string rawPassword, bool signUp)
        {
            username = (username ?? "").Trim();
            string error = ValidateUsername(username);
            if (error != null)
                throw new UgsException(error);
            string derived = DerivePassword(username, rawPassword);
            string captured = username;
            return RunExclusive(async () =>
            {
                Set(AccountState.SigningIn, null);
                try
                {
                    await EnsureProfileAsync(AuthProfileFor(captured));
                    // UGS usernames are case-insensitive — pass the canonical lowercase
                    // form so the account matches the password derivation salt.
                    string canonical = captured.ToLowerInvariant();
                    if (signUp)
                        await AuthenticationService.Instance.SignUpWithUsernamePasswordAsync(canonical, derived);
                    else
                        await AuthenticationService.Instance.SignInWithUsernamePasswordAsync(canonical, derived);
                    AddKnown(captured);
                    CompleteSignIn(captured);
                }
                catch (Exception e)
                {
                    Set(AccountState.SignedOut, null);
                    throw Normalize(e, signUp ? AccountOp.SignUp : AccountOp.Login);
                }
            });
        }

        /// <summary>Switch to another known account via its cached session token.</summary>
        public static Task SwitchToAsync(string username)
        {
            string captured = (username ?? "").Trim();
            if (captured.Length == 0)
                throw new UgsException(UsernameRuleError);
            return RunExclusive(async () =>
            {
                Set(AccountState.SigningIn, null);
                try
                {
                    await EnsureProfileAsync(AuthProfileFor(captured));
                    if (!AuthenticationService.Instance.SessionTokenExists)
                        throw new UgsException("Session expired — enter the password for this account.");
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    CompleteSignIn(captured);
                }
                catch (Exception e)
                {
                    Set(AccountState.SignedOut, null);
                    throw Normalize(e, AccountOp.Restore);
                }
            });
        }

        /// <summary>Sign out and clear the account's cached credentials. The username
        /// stays in the known list; the mode stays "account".</summary>
        public static void Logout()
        {
            if (UnityServices.State == ServicesInitializationState.Initialized &&
                AuthenticationService.Instance.IsSignedIn)
                AuthenticationService.Instance.SignOut(clearCredentials: true);
            PlayerPrefs.SetString(Salted(ActiveKeyPrefix), "");
            PlayerPrefs.Save();
            _playerId = null;
            Set(AccountState.SignedOut, null);
            Debug.Log("[Account] logged out");
        }

        /// <summary>Persist the explicit no-account choice.</summary>
        public static void ChooseGuest()
        {
            if (UnityServices.State == ServicesInitializationState.Initialized &&
                AuthenticationService.Instance.IsSignedIn)
                AuthenticationService.Instance.SignOut(clearCredentials: false); // the account can return
            PlayerPrefs.SetString(Salted(ModeKeyPrefix), "guest");
            PlayerPrefs.SetString(Salted(ActiveKeyPrefix), "");
            PlayerPrefs.Save();
            _playerId = null;
            Set(AccountState.Guest, null);
            Debug.Log("[Account] guest mode chosen");
        }

        /// <summary>
        /// Gate for going online (NetLauncher awaits this before any Relay call):
        /// SignedIn passes; SignedInOffline retries the restore once; guests and
        /// signed-out players are refused — except the editor dev fallback, which keeps
        /// the legacy anonymous sign-in so MPPM/AutoClientDriver flows work unchanged.
        /// </summary>
        public static async Task EnsureOnlineAsync()
        {
            // A boot or user-initiated op may still be settling — wait it out first.
            for (int guard = 0; guard < 4 && _state == AccountState.SigningIn; guard++)
            {
                Task pending = _op != null && !_op.IsCompleted ? _op : _boot;
                if (pending == null || pending.IsCompleted) break;
                try { await pending; } catch { /* surfaced by that op's own caller */ }
            }

            switch (_state)
            {
                case AccountState.SignedIn:
                    return;

                case AccountState.SignedInOffline:
                    await RunExclusive(RetryRestoreAsync);
                    return;

                case AccountState.Guest:
#if UNITY_EDITOR
                    await RunExclusive(EditorAnonymousSignInAsync);
                    return;
#else
                    throw new UgsException("An account is required to play online.");
#endif

                default:
#if UNITY_EDITOR
                    // A fresh editor/MPPM virtual player that never made the
                    // account-or-guest choice also gets the dev fallback.
                    if (_state == AccountState.SignedOut && FirstRunChoicePending)
                    {
                        await RunExclusive(EditorAnonymousSignInAsync);
                        return;
                    }
#endif
                    throw new UgsException("An account is required to play online.");
            }
        }

        // ------------------------------------------------------------------ internals

        private enum AccountOp { SignUp, Login, Restore }

        private static void Set(AccountState state, string username)
        {
            bool changed = _state != state || _username != username;
            _state = state;
            _username = username;
            if (changed)
                Changed?.Invoke();
        }

        /// <summary>Serialize the async ops — the UI disables its buttons while one
        /// runs, so a concurrent call is always a bug or a double-click.</summary>
        private static Task RunExclusive(Func<Task> op)
        {
            if (_op != null && !_op.IsCompleted)
                throw new UgsException("Please wait…");
            _op = op();
            return _op;
        }

        /// <summary>Initialize UGS once, then land signed OUT on the target profile
        /// (SwitchProfile and the sign-in/up calls all require the signed-out state).</summary>
        private static async Task EnsureProfileAsync(string profile)
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetProfile(profile);
                await UnityServices.InitializeAsync(options);
            }
            var auth = AuthenticationService.Instance;
            if (auth.IsSignedIn)
                auth.SignOut(clearCredentials: false);
            if (auth.Profile != profile)
                auth.SwitchProfile(profile);
        }

        private static void CompleteSignIn(string username)
        {
            var auth = AuthenticationService.Instance;
            _playerId = auth.PlayerId;
            PlayerPrefs.SetString(Salted(PlayerIdKeyPrefix + LocalProfileFor(username)), _playerId ?? "");
            PlayerPrefs.SetString(Salted(ModeKeyPrefix), "account");
            PlayerPrefs.SetString(Salted(ActiveKeyPrefix), username);
            PlayerPrefs.Save();
            ClientIdentity.PlayerName = username;
            Set(AccountState.SignedIn, username);
            Debug.Log("[Account] signed in as " + username +
                      " (player " + _playerId + ", profile " + auth.Profile + ")");
        }

        /// <summary>SignedInOffline → one restore attempt; transport failure keeps the
        /// offline state, an auth rejection demotes to SignedOut with the notice.</summary>
        private static async Task RetryRestoreAsync()
        {
            string username = _username;
            try
            {
                await EnsureProfileAsync(AuthProfileFor(username));
                if (AuthenticationService.Instance.SessionTokenExists)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    CompleteSignIn(username);
                    return;
                }
            }
            catch (Exception e)
            {
                if (!IsAuthRejection(e))
                    throw new UgsException("No internet connection.", e);
            }
            _pendingNotice = SessionExpiredNotice;
            Set(AccountState.SignedOut, null);
            throw new UgsException(SessionExpiredNotice);
        }

#if UNITY_EDITOR
        /// <summary>The legacy anonymous sign-in (ported from UgsGateway), EDITOR ONLY:
        /// keeps MPPM virtual players and AutoClientDriver working without accounts.</summary>
        private static async Task EditorAnonymousSignInAsync()
        {
            Debug.LogWarning("[Account] EDITOR dev fallback: anonymous UGS sign-in on profile " +
                             ClientIdentity.AuthProfile + " (builds require an account to play online)");
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    var options = new InitializationOptions();
                    options.SetProfile(ClientIdentity.AuthProfile);
                    await UnityServices.InitializeAsync(options);
                }
                var auth = AuthenticationService.Instance;
                if (auth.Profile != ClientIdentity.AuthProfile)
                {
                    if (auth.IsSignedIn)
                        auth.SignOut(clearCredentials: false);
                    auth.SwitchProfile(ClientIdentity.AuthProfile);
                }
                if (!auth.IsSignedIn)
                    await auth.SignInAnonymouslyAsync();
                Debug.Log("[Account] UGS signed in (profile " + ClientIdentity.AuthProfile +
                          ", player " + auth.PlayerId + ")");
            }
            catch (Exception e)
            {
                throw Normalize(e, AccountOp.Restore);
            }
        }
#endif

        /// <summary>
        /// Derive the UGS password from the player's raw password. FROZEN FOREVER:
        /// changing anything here (prefix, salt line, hash, substitutions, length)
        /// locks every existing account out of its UGS credentials. NEVER change it.
        /// </summary>
        private static string DerivePassword(string username, string raw)
        {
            if (string.IsNullOrEmpty(raw))
                throw new UgsException("Enter a password.");
            if (raw.Length > 64)
                raw = raw.Substring(0, 64);
            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(
                "pascension.account.v1\n" + username.Trim().ToLowerInvariant() + "\n" + raw));
            // "aA1!" satisfies the UGS upper/lower/digit/symbol rules; total 24 chars.
            return "aA1!" + Convert.ToBase64String(digest).Replace('+', 'x').Replace('/', 'y').Substring(0, 20);
        }

        /// <summary>UGS auth profile for an account (matches [a-zA-Z0-9_-]{1,30}).
        /// The editor variant is MPPM-disjoint, mirroring ClientIdentity.AuthProfile.</summary>
        private static string AuthProfileFor(string username)
        {
            string user = Fnv(username.Trim().ToLowerInvariant());
#if UNITY_EDITOR
            return "vp" + Fnv(Application.dataPath) + "a" + user;
#else
            return "acct" + user;
#endif
        }

        private static string LocalProfileFor(string username) =>
            "acct" + Fnv(username.Trim().ToLowerInvariant());

        private static string Fnv(string s) => ClientIdentity.StableHash(s).ToString("x8");

        private static string Salted(string key)
        {
#if UNITY_EDITOR
            // Same scheme as ClientIdentity.GuidKey: MPPM virtual players share the
            // editor PlayerPrefs store but have distinct project data paths.
            return key + "." + ClientIdentity.StableHash(Application.dataPath);
#else
            return key;
#endif
        }

        private static void AddKnown(string username)
        {
            var list = new List<string>(KnownAccounts);
            list.RemoveAll(n => string.Equals(n, username, StringComparison.OrdinalIgnoreCase));
            list.Add(username);
            PlayerPrefs.SetString(Salted(KnownKeyPrefix), string.Join("\n", list));
            PlayerPrefs.Save();
        }

        private static string EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static bool IsAuthRejection(Exception e) =>
            e is RequestFailedException failed &&
            (failed.ErrorCode == CommonErrorCodes.InvalidToken ||
             failed.ErrorCode == AuthenticationErrorCodes.InvalidSessionToken ||
             failed.ErrorCode == AuthenticationErrorCodes.ClientNoActiveSession ||
             failed.ErrorCode == AuthenticationErrorCodes.BannedUser);

        /// <summary>Map SDK failures to user-readable messages (raw detail logged).</summary>
        private static UgsException Normalize(Exception e, AccountOp op)
        {
            if (e is UgsException already) return already;
            Debug.LogWarning("[Account] UGS auth failure (" + op + "): " + e);
            if (e is RequestFailedException failed)
            {
                if (failed.ErrorCode == CommonErrorCodes.TransportError ||
                    failed.ErrorCode == CommonErrorCodes.Timeout ||
                    failed.ErrorCode == CommonErrorCodes.ServiceUnavailable)
                    return new UgsException("No internet connection.", e);
                if (op == AccountOp.Login && failed.ErrorCode == AuthenticationErrorCodes.InvalidParameters)
                    return new UgsException("Wrong username or password.", e);
                if (op == AccountOp.SignUp &&
                    (failed.ErrorCode == AuthenticationErrorCodes.AccountAlreadyLinked ||
                     failed.ErrorCode == CommonErrorCodes.Conflict))
                    return new UgsException("That username is already taken.", e);
                if (IsAuthRejection(e))
                    return new UgsException(SessionExpiredNotice, e);
            }
            return new UgsException(
                "Online services unavailable — is Username/Password sign-in enabled for this project?", e);
        }
    }
}
