using System;
using System.Reflection;
using HarmonyLib;

namespace TestRig
{
    /// <summary>
    ///     Per-instance player identity, for running several clients on one machine.
    ///
    ///     Stationeers does not get a player's identity from Steam at join time. It reads
    ///     <c>PlayerCookie-v2.xml</c> out of <c>Application.persistentDataPath</c> and honours it
    ///     verbatim whenever <c>Version == 2 &amp;&amp; ClientId != 0</c>; the server's
    ///     <c>VerifyConnection</c> then checks only blacklist, password and exact game-version
    ///     string. So a second client only needs a different <c>ulong</c> in that one field, and
    ///     both <c>PlayerCookie.ClientId</c> and <c>.Username</c> have public setters.
    ///
    ///     Two clients that present the same id are NOT merely cosmetically wrong. The server keys
    ///     a player's body on it (<c>Brain.PlayerBrains</c> is a <c>Dictionary&lt;ulong, Brain&gt;</c>
    ///     whose <c>RegisterBrain</c> does a silent overwrite), so the second joiner resolves onto
    ///     the first joiner's character. Distinct ids are mandatory.
    ///
    ///     <c>persistentDataPath</c> is per-Windows-user, so instances can end up sharing one cookie
    ///     FILE even when they present different ids. Writing a synthetic id into the developer's
    ///     real cookie would be a genuine loss, and <c>PlayerCookie.Save()</c> fires on triggers as
    ///     innocuous as pressing Esc in a running world. This class therefore also suppresses
    ///     <c>Save()</c> whenever an override is in force.
    /// </summary>
    internal static class Identity
    {
        internal static ulong OverrideClientId;      // 0 = no override
        internal static string OverrideUsername;     // null/empty = no override

        internal static bool Applied;
        internal static int ApplyCount;
        internal static int SuppressedSaves;
        internal static string LastError;

        internal static bool HasOverride =>
            OverrideClientId != 0 || !string.IsNullOrEmpty(OverrideUsername);

        /// <summary>
        ///     The id this instance will actually present, override or not. This is what a peer
        ///     comparison has to use: an instance with no override still has an identity (the
        ///     developer's real cookie), and two instances that BOTH decline to override are the
        ///     most obvious duplicate-identity case there is.
        /// </summary>
        internal static ulong EffectiveClientId
        {
            get
            {
                if (OverrideClientId != 0) return OverrideClientId;
                try
                {
                    object cookie = CurrentCookie();
                    if (cookie == null) return 0;
                    var prop = AccessTools.Property(cookie.GetType(), "ClientId");
                    if (prop == null) return 0;
                    return Convert.ToUInt64(prop.GetValue(cookie, null));
                }
                catch { return 0; }
            }
        }

        /// <summary>The name this instance will present, override or not.</summary>
        internal static string EffectiveUsername
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideUsername)) return OverrideUsername;
                try
                {
                    object cookie = CurrentCookie();
                    if (cookie == null) return null;
                    var prop = AccessTools.Property(cookie.GetType(), "Username");
                    return prop == null ? null : prop.GetValue(cookie, null) as string;
                }
                catch { return null; }
            }
        }

        private static Type _networkManager;
        private static PropertyInfo _cookieProp;

        private static Type NetworkManagerType =>
            _networkManager ?? (_networkManager =
                AccessTools.TypeByName("Assets.Scripts.Networking.NetworkManager"));

        private static PropertyInfo CookieProp =>
            _cookieProp ?? (_cookieProp = NetworkManagerType == null
                ? null
                : AccessTools.Property(NetworkManagerType, "Cookie"));

        internal static object CurrentCookie()
        {
            try { return CookieProp?.GetValue(null); }
            catch { return null; }
        }

        /// <summary>
        ///     Push the configured identity onto the live cookie. Safe to call repeatedly and safe
        ///     to call before the cookie exists; it reports whether it actually landed.
        /// </summary>
        internal static bool Apply()
        {
            if (!HasOverride) return false;
            try
            {
                object cookie = CurrentCookie();
                if (cookie == null)
                {
                    // Batch mode leaves Cookie null on purpose, and a client with a null cookie
                    // joins as ClientId 0 with an empty name. Build one so the rig still works
                    // there; the property setter is private, so go through the backing field.
                    Type cookieType = AccessTools.TypeByName("Networking.Servers.PlayerCookie")
                                      ?? AccessTools.TypeByName("PlayerCookie");
                    if (cookieType == null) { LastError = "PlayerCookie type not found"; return false; }
                    cookie = Activator.CreateInstance(cookieType);
                    AccessTools.Property(cookieType, "Version")?.SetValue(cookie, 2);
                    if (CookieProp == null || !CookieProp.CanWrite)
                    {
                        var backing = AccessTools.Field(NetworkManagerType, "<Cookie>k__BackingField");
                        if (backing == null) { LastError = "cannot assign NetworkManager.Cookie"; return false; }
                        backing.SetValue(null, cookie);
                    }
                    else { CookieProp.SetValue(null, cookie); }
                }

                Type t = cookie.GetType();
                if (OverrideClientId != 0)
                    AccessTools.Property(t, "ClientId")?.SetValue(cookie, OverrideClientId);
                if (!string.IsNullOrEmpty(OverrideUsername))
                    AccessTools.Property(t, "Username")?.SetValue(cookie, OverrideUsername);

                Applied = true;
                ApplyCount++;
                LastError = null;
                Plugin.Log.LogWarning("identity override applied: ClientId=" + OverrideClientId +
                                      " Username=" + OverrideUsername);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.Log.LogError("identity override failed: " + ex);
                return false;
            }
        }
    }

    /// <summary>
    ///     <c>NetworkManager.Init(TransportType)</c> is where the cookie is loaded, from
    ///     <c>GameManager.Awake</c>. A postfix here is the earliest point at which the identity can
    ///     be rewritten, and it is long before anything reads <c>LocalClientId</c> (the join
    ///     handshake copies it into <c>VerifyPlayerMessage</c> at connect time).
    /// </summary>
    [HarmonyPatch]
    internal static class NetworkManagerInitPatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("Assets.Scripts.Networking.NetworkManager");
            return type == null ? null : AccessTools.Method(type, "Init");
        }

        internal static MethodBase TargetMethod() => Resolve();
        internal static bool Prepare() => Plugin.ClientOnlyPatches && Resolve() != null;

        internal static void Postfix()
        {
            try { if (Identity.HasOverride) Identity.Apply(); }
            catch { }
        }
    }

    /// <summary>
    ///     Never let a driven instance persist a synthetic identity.
    ///
    ///     <c>PlayerCookie.Save()</c> writes <c>persistentDataPath\PlayerCookie-v2.xml</c>, which is
    ///     per-Windows-user and therefore shared by every instance unless the install's
    ///     <c>app.info</c> separates it. Its triggers include dismissing the old-save and
    ///     major-update popups and opening the in-game menu with Esc, so an override could leak into
    ///     the developer's real cookie during ordinary play. Skipping the original is safe: the
    ///     cookie is only ever read back at startup.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerCookieSavePatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("Networking.Servers.PlayerCookie")
                       ?? AccessTools.TypeByName("PlayerCookie");
            return type == null ? null : AccessTools.Method(type, "Save");
        }

        internal static MethodBase TargetMethod() => Resolve();
        internal static bool Prepare() => Plugin.ClientOnlyPatches && Resolve() != null;

        internal static bool Prefix()
        {
            if (!Identity.HasOverride && !Plugin.LockCookieFileValue) return true;   // run the original
            Identity.SuppressedSaves++;
            Plugin.Log.LogWarning("PlayerCookie.Save() suppressed (identity override or cookie lock active)");
            return false;
        }
    }
}
