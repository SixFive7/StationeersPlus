using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using GameManager = Assets.Scripts.GameManager;
using NetworkClient = Assets.Scripts.NetworkClient;
using NetworkManager = Assets.Scripts.Networking.NetworkManager;

namespace TestRig
{
    /// <summary>
    ///     A recorder for one Direct Connect attempt, from the call into the game to whatever ended
    ///     it.
    ///
    ///     It exists because the join path fails SILENTLY, for two independent reasons:
    ///
    ///     <list type="number">
    ///       <item><c>NetworkManager.ReceiveEvents</c> switches on the RakNet packet id and carries a
    ///             case for exactly five of them (ConnectionRequestAccepted, NewIncomingConnection,
    ///             NoFreeIncomingConnections, DisconnectionNotification, ConnectionLost). Every id
    ///             that reports a REFUSED or ABANDONED attempt (ConnectionAttemptFailed=17,
    ///             AlreadyConnected=18, ConnectionBanned=23, InvalidPassword=24,
    ///             IncompatibleProtocolVersion=25, IpRecentlyConnected=26) falls off the end of that
    ///             switch with no console line and no state change. A joiner whose attempt RakNet
    ///             gave up on is indistinguishable from one still waiting.</item>
    ///       <item>Everything the game does record about a failed attempt is UNDONE before anyone can
    ///             read it. <c>/connect</c>'s own timeout calls <c>NetworkClient.Cancel</c>, which
    ///             reaches <c>NetworkManager.EndConnection</c> and <c>ShutDownRaknet</c>: NetworkRole
    ///             goes back to None, NetworkState to Offline, GameState to None, and the RakNet peer
    ///             is disposed with its UDP socket. Reading <c>/status</c> or netstat after the call
    ///             has returned therefore CANNOT distinguish "a socket was never opened" from "a
    ///             socket was opened, used, and torn down". That ambiguity sent an earlier
    ///             investigation after a bug that did not exist.</item>
    ///     </list>
    ///
    ///     So this class samples state DURING the attempt rather than after it, and hooks the methods
    ///     that would otherwise take their evidence with them. The single most valuable line it emits
    ///     is the caller stack on <c>ShutDownRaknet</c>: it names who destroyed the socket and at what
    ///     millisecond, which no after-the-fact inspection can recover.
    ///
    ///     Everything here is diagnostic. No hook is a prefix that can skip its original, none mutates
    ///     game state, none reads an argument by name (see <see cref="Install"/>), and every body is
    ///     wrapped so a fault in the recorder cannot break the join it is only supposed to watch.
    /// </summary>
    internal static class JoinTrace
    {
        /// <summary>Ring cap. Only state CHANGES are recorded, so a three-minute join produces a
        /// handful of lines and this is a backstop rather than the normal limit.</summary>
        private const int MaxEvents = 400;

        private const int SampleIntervalMs = 200;

        private static readonly object _gate = new object();
        private static readonly List<string> _events = new List<string>();

        private static bool _armed;
        private static DateTime _t0 = DateTime.UtcNow;
        private static string _target;
        private static int _dropped;
        private static string _lastSample;
        private static DateTime _lastSampleAt = DateTime.MinValue;

        internal static bool IsArmed { get { lock (_gate) { return _armed; } } }

        /// <summary>
        ///     Starts a recording. Called by <c>/connect</c> immediately before
        ///     <c>JoinClientFromMenu</c>, so t=0 is the moment the game was asked to join and every
        ///     offset afterwards is directly comparable across runs.
        /// </summary>
        internal static void Arm(string target)
        {
            lock (_gate)
            {
                _events.Clear();
                _dropped = 0;
                _t0 = DateTime.UtcNow;
                _target = target;
                _lastSample = null;
                _lastSampleAt = DateTime.MinValue;
                _armed = true;
            }
        }

        internal static void Disarm()
        {
            lock (_gate) { _armed = false; }
        }

        /// <summary>Appends one event. Safe from any thread; does nothing while disarmed.</summary>
        internal static void Note(string kind, string detail)
        {
            try
            {
                lock (_gate)
                {
                    if (!_armed) return;
                    if (_events.Count >= MaxEvents) { _dropped++; return; }
                    long ms = (long)(DateTime.UtcNow - _t0).TotalMilliseconds;
                    _events.Add(new Json.Obj()
                        .Int("ms", ms)
                        .Str("kind", kind)
                        .Str("detail", detail)
                        .ToString());
                }
            }
            catch { }
        }

        /// <summary>
        ///     Per-frame sampler, driven from the same <c>ImGuiManager.LateUpdate</c> postfix that
        ///     drains the main-thread pump. Main thread only, which is what makes the RakNet reads
        ///     below safe: <c>ShutDownRaknet</c> disposes the peer and nulls the handle on that same
        ///     thread, so a probe can never observe a freed instance.
        ///
        ///     Records a line only when the composite state string CHANGES.
        /// </summary>
        internal static void Tick()
        {
            try
            {
                lock (_gate)
                {
                    if (!_armed) return;
                    if ((DateTime.UtcNow - _lastSampleAt).TotalMilliseconds < SampleIntervalMs) return;
                    _lastSampleAt = DateTime.UtcNow;
                }

                string line = StateLine();
                lock (_gate)
                {
                    if (line == _lastSample) return;
                    _lastSample = line;
                }
                Note("state", line);
            }
            catch { }
        }

        /// <summary>
        ///     The one-line state of everything that moves during a join. Main thread only.
        /// </summary>
        internal static string StateLine()
        {
            var sb = new System.Text.StringBuilder();
            try { sb.Append("gameState=").Append(GameManager.GameState.ToString()); } catch { sb.Append("gameState=?"); }
            try { sb.Append(" networkRole=").Append(NetworkManager.NetworkRole.ToString()); } catch { }
            try { sb.Append(" networkState=").Append(NetworkManager.NetworkState.ToString()); } catch { }
            sb.Append(" peer=").Append(ProbePeer().Summary);
            return sb.ToString();
        }

        // ---- report ----------------------------------------------------------

        internal static string DescribeJson()
        {
            string[] events;
            bool armed;
            int dropped;
            string target;
            long elapsed;
            lock (_gate)
            {
                events = _events.ToArray();
                armed = _armed;
                dropped = _dropped;
                target = _target;
                elapsed = (long)(DateTime.UtcNow - _t0).TotalMilliseconds;
            }

            return new Json.Obj()
                .Bit("armed", armed)
                .Str("target", target)
                .Int("elapsedMs", elapsed)
                .Int("events", events.Length)
                .Int("droppedEvents", dropped)
                .Bit("patched", PatchesApplied)
                .StrArray("hooks", InstallReport)
                .Raw("trace", "[" + string.Join(",", events) + "]")
                .ToString();
        }

        // ---- hook installation -----------------------------------------------

        /// <summary>
        ///     True only when both load-bearing hooks took. A silently unpatched trace produces an
        ///     empty log that reads exactly like "nothing happened", which is the one answer a
        ///     diagnostic must never give by accident.
        /// </summary>
        internal static bool PatchesApplied;

        /// <summary>One line per hook: the target and whether it took, or why it did not.</summary>
        internal static readonly List<string> InstallReport = new List<string>();

        /// <summary>
        ///     Installs the hooks one at a time, each in its own try/catch.
        ///
        ///     Deliberately NOT done through <c>[HarmonyPatch]</c> attributes and <c>PatchAll</c>: one
        ///     bad target there throws out of the single <c>PatchAll</c> call and takes every patch
        ///     after it with it, including the console tap and the input chain this plugin depends on.
        ///     A diagnostic must not be able to break the tool it is diagnosing with.
        ///
        ///     For the same reason no hook takes an argument of the original by name. Harmony matches
        ///     those by parameter name and throws at patch time on a mismatch; the only injections
        ///     used here are <c>__result</c> and <c>__exception</c>, which are Harmony's own and do
        ///     not depend on the game's metadata. The join target string is already recorded by
        ///     <see cref="Arm"/>, and everything else the arguments would have carried is either in
        ///     the state line or in the console tee.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            InstallReport.Clear();
            if (harmony == null) { InstallReport.Add("no harmony instance"); return; }

            bool startClient = Hook(harmony, typeof(NetworkManager), "StartClient",
                new[] { typeof(string), typeof(ushort), typeof(ushort) },
                nameof(StartClientEnter), nameof(StartClientReturned), nameof(StartClientThrew));

            bool shutdown = Hook(harmony, typeof(NetworkManager), "ShutDownRaknet", Type.EmptyTypes,
                nameof(ShutDownRaknetEnter), null, null);

            Hook(harmony, typeof(NetworkClient), "JoinClientFromMenu", new[] { typeof(string) },
                nameof(JoinEnter), nameof(JoinReturned), nameof(JoinThrew));

            Hook(harmony, typeof(NetworkClient), "OnJoinFailed", new[] { typeof(string) },
                nameof(OnJoinFailedEnter), null, null);

            Hook(harmony, typeof(NetworkManager), "EndConnection", Type.EmptyTypes,
                nameof(EndConnectionEnter), null, null);

            Hook(harmony, typeof(NetworkClient), "Connected", null,
                null, nameof(ClientConnectedReturned), null);

            PatchesApplied = startClient && shutdown;
        }

        private static bool Hook(Harmony harmony, Type owner, string method, Type[] signature,
                                 string prefix, string postfix, string finalizer)
        {
            string label = owner.Name + "." + method;
            try
            {
                MethodBase target = signature == null
                    ? AccessTools.Method(owner, method)
                    : AccessTools.Method(owner, method, signature);
                if (target == null)
                {
                    InstallReport.Add(label + ": target not found");
                    return false;
                }
                // PatchProcessor rather than harmony.Patch(...): the five-argument overload that
                // takes a finalizer positionally is marked obsolete in the HarmonyX this game ships.
                var processor = harmony.CreateProcessor(target);
                if (prefix != null) processor.AddPrefix(AccessTools.Method(typeof(JoinTrace), prefix));
                if (postfix != null) processor.AddPostfix(AccessTools.Method(typeof(JoinTrace), postfix));
                if (finalizer != null) processor.AddFinalizer(AccessTools.Method(typeof(JoinTrace), finalizer));
                processor.Patch();
                InstallReport.Add(label + ": ok");
                return true;
            }
            catch (Exception ex)
            {
                InstallReport.Add(label + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ---- the hooks --------------------------------------------------------

        /// <summary>Entry to the join, which is where the target string is parsed into one of three
        /// completely different paths: a 17-character single token goes to Steam P2P, anything that
        /// is not exactly two colon-separated parts pops a dialog and returns, and only the two-part
        /// case reaches StartClient.</summary>
        private static void JoinEnter()
        {
            Note("joinClientFromMenu.enter",
                StateLine() + " isNewTutorial=" + GameManager.IsNewTutorial);
        }

        private static void JoinReturned()
        {
            Note("joinClientFromMenu.returned", StateLine());
        }

        private static void JoinThrew(Exception __exception)
        {
            if (__exception != null) Note("joinClientFromMenu.threw", __exception.ToString());
        }

        /// <summary>The method the whole question is about.</summary>
        private static void StartClientEnter()
        {
            Note("startClient.enter", StateLine());
        }

        /// <summary>
        ///     <c>__result</c> is the single value that separates "the client never got a socket" from
        ///     "the client got a socket and a connection attempt, and nothing came back". StartClient
        ///     returns true only after <c>rakNet.Startup</c> reported RaknetStarted AND
        ///     <c>rakNet.Connect</c> reported ConnectionAttemptStarted, and it assigns
        ///     <c>NetworkClient.Address</c> on that same path.
        /// </summary>
        private static void StartClientReturned(bool __result)
        {
            Note("startClient.returned", "result=" + __result + " " + StateLine());
        }

        private static void StartClientThrew(Exception __exception)
        {
            if (__exception != null) Note("startClient.threw", __exception.ToString());
        }

        /// <summary>Every managed-side join failure runs through here, and it shuts RakNet down. Its
        /// presence means the GAME decided the attempt was over; its absence during a timeout means
        /// nothing in the game ever noticed.</summary>
        private static void OnJoinFailedEnter()
        {
            Note("onJoinFailed", "via " + Caller(6));
        }

        /// <summary>
        ///     The line that matters most. This is where the UDP socket goes away: Shutdown, Dispose,
        ///     and the handle set back to default. An inspection performed after a failed
        ///     <c>/connect</c> finds no socket because this already ran, not because one was never
        ///     created. The millisecond and the caller turn that from an inference into a fact.
        /// </summary>
        private static void ShutDownRaknetEnter()
        {
            Note("shutDownRaknet", "peerBefore=" + ProbePeer().Summary + " via " + Caller(8));
        }

        /// <summary>Where NetworkRole returns to None and NetworkState to Offline.</summary>
        private static void EndConnectionEnter()
        {
            Note("endConnection", StateLine() + " via " + Caller(8));
        }

        /// <summary>The success marker. <c>NetworkClient.Connected</c> runs from the
        /// ConnectionRequestAccepted branch of <c>ReceiveEvents</c>, so its presence proves the RakNet
        /// handshake completed and any later failure is above the transport.</summary>
        private static void ClientConnectedReturned()
        {
            Note("clientConnected",
                "hostConnectionId=" + NetworkClient.HostConnectionId +
                " method=" + NetworkClient.ConnectionMethod + " " + StateLine());
        }

        // ---- RakNet peer probe -----------------------------------------------

        /// <summary>What one probe of the live RakNet peer found.</summary>
        internal sealed class PeerState
        {
            internal bool ManagerPresent;
            internal bool HandleNull = true;
            internal bool? Active;
            internal int Connections = -1;
            internal readonly List<string> Slots = new List<string>();
            internal string Error;

            /// <summary>Compact form for the transition log.</summary>
            internal string Summary
            {
                get
                {
                    if (Error != null) return "error(" + Error + ")";
                    if (!ManagerPresent) return "noManager";
                    if (HandleNull) return "null";
                    string s = Active.HasValue ? (Active.Value ? "active" : "inactive") : "active?";
                    s += ",conns=" + Connections.ToString(CultureInfo.InvariantCulture);
                    if (Slots.Count > 0) s += ",[" + string.Join(" ", Slots.ToArray()) + "]";
                    return s;
                }
            }

            internal string ToJson()
            {
                var o = new Json.Obj()
                    .Bit("managerPresent", ManagerPresent)
                    .Bit("handleNull", HandleNull)
                    .Int("connections", Connections)
                    .StrArray("slots", Slots)
                    .Str("error", Error);
                if (Active.HasValue) o.Bit("active", Active.Value);
                else o.Raw("active", "null");
                return o.ToString();
            }
        }

        /// <summary>
        ///     Reads the live <c>NetworkManager.rakNet</c> peer. MAIN THREAD ONLY.
        ///
        ///     <c>active</c> is <c>RakPeerInterface.IsActive</c>, the managed-side answer to "is a UDP
        ///     socket bound right now". It is the value netstat cannot supply, because a blocking
        ///     <c>/connect</c> only returns after its own cleanup has disposed the peer.
        ///
        ///     <c>slots</c> is RakNet's own remote-system table: index, connection state, address.
        ///     During a live attempt the target appears as <c>IsConnecting</c>; when RakNet abandons
        ///     it (12 sends 500 ms apart, so about 6 s at the defaults <c>StartClient</c> passes) the
        ///     entry disappears. That transition with nothing else changing IS the signature of a
        ///     connection the host never answered.
        ///
        ///     Resolved reflectively rather than by referencing Brutal.RakNet, so a rename degrades
        ///     this to "error" instead of preventing the plugin from loading. The same choice keeps
        ///     the plugin free of a compile-time dependency on a native-interop assembly it has no
        ///     other business with.
        /// </summary>
        internal static PeerState ProbePeer()
        {
            var state = new PeerState();
            try
            {
                if (!EnsureReflection()) { state.Error = _reflectError; return state; }

                object manager = _fInstance.GetValue(null);
                if (manager == null) return state;
                state.ManagerPresent = true;

                // Read the handle FRESH every probe. It is a struct wrapping a native pointer, and
                // ShutDownRaknet both disposes it and assigns default; a cached copy would be a
                // use-after-free the next time this ran.
                object peer = _fRakNet.GetValue(manager);
                if (peer == null) return state;

                object nullCheck = _mIsNull.Invoke(peer, null);
                state.HandleNull = nullCheck is bool && (bool)nullCheck;
                if (state.HandleNull) return state;

                var one = new object[] { peer };
                if (_mIsActive != null) state.Active = ToBool(_mIsActive.Invoke(null, one));
                if (_mNumberOfConnections != null)
                    state.Connections = Convert.ToInt32(_mNumberOfConnections.Invoke(null, one), CultureInfo.InvariantCulture);

                if (_mGetSystemAddressFromIndex == null || _mGetConnectionState == null || _mAddressFrom == null)
                    return state;

                uint max = 1;
                if (_mGetMaximumNumberOfPeers != null)
                    max = Convert.ToUInt32(_mGetMaximumNumberOfPeers.Invoke(null, one), CultureInfo.InvariantCulture);
                if (max > 8) max = 8;

                for (uint i = 0; i < max; i++)
                {
                    object addr = _mGetSystemAddressFromIndex.Invoke(null, new object[] { peer, i });
                    if (addr == null) continue;
                    object identifier = _mAddressFrom.Invoke(null, new object[] { addr });
                    object connState = _mGetConnectionState.Invoke(null, new object[] { peer, identifier });
                    string name = connState == null ? "?" : connState.ToString();
                    // IsNotConnected is the resting value of an unused slot; reporting eight of them
                    // every sample would bury the one that matters.
                    if (name == "IsNotConnected") continue;
                    state.Slots.Add(i.ToString(CultureInfo.InvariantCulture) + ":" + name + "@" + AddressText(addr));
                }
            }
            catch (Exception ex) { state.Error = ex.GetType().Name + ": " + ex.Message; }
            return state;
        }

        private static string AddressText(object systemAddress)
        {
            try
            {
                if (_mAddressToString == null) return "?";
                // The game's own SystemAddress.ToString does not byte-swap SinPort, so the port here
                // is the raw network-order value. The ADDRESS identifies the slot; the port is only
                // there to tell two targets apart.
                object s = _mAddressToString.Invoke(systemAddress, new object[] { true, ':' });
                return s == null ? "?" : s.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>
        ///     Unboxes RakNet's <c>Bool8</c>. That type exists twice in the assembly with different
        ///     backing fields (one <c>char</c>, one <c>byte</c>), so the implicit conversion operator
        ///     is preferred over reading a field whose name and type would have to be guessed.
        /// </summary>
        private static bool? ToBool(object boxed)
        {
            if (boxed == null) return null;
            if (boxed is bool) return (bool)boxed;
            try
            {
                var t = boxed.GetType();
                var op = AccessTools.Method(t, "op_Implicit", new[] { t });
                if (op != null && op.ReturnType == typeof(bool))
                    return (bool)op.Invoke(null, new[] { boxed });
            }
            catch { }
            try
            {
                foreach (var f in boxed.GetType().GetFields(AccessTools.all))
                {
                    if (f.IsStatic) continue;
                    return Convert.ToInt64(f.GetValue(boxed), CultureInfo.InvariantCulture) != 0;
                }
            }
            catch { }
            return null;
        }

        // ---- reflection cache -------------------------------------------------

        private static bool _reflectTried;
        private static bool _reflectOk;
        private static string _reflectError;

        private static FieldInfo _fInstance;
        private static FieldInfo _fRakNet;
        private static MethodInfo _mIsNull;
        private static MethodInfo _mIsActive;
        private static MethodInfo _mNumberOfConnections;
        private static MethodInfo _mGetMaximumNumberOfPeers;
        private static MethodInfo _mGetSystemAddressFromIndex;
        private static MethodInfo _mGetConnectionState;
        private static MethodInfo _mAddressFrom;
        private static MethodInfo _mAddressToString;

        private static bool EnsureReflection()
        {
            if (_reflectTried) return _reflectOk;
            _reflectTried = true;
            try
            {
                _fInstance = AccessTools.DeclaredField(typeof(NetworkManager), "Instance");
                _fRakNet = AccessTools.DeclaredField(typeof(NetworkManager), "rakNet");
                if (_fInstance == null || _fRakNet == null)
                {
                    _reflectError = "NetworkManager.Instance or NetworkManager.rakNet not found";
                    return false;
                }

                _mIsNull = AccessTools.Method(_fRakNet.FieldType, "IsNull");

                var api = AccessTools.TypeByName("Brutal.RakNetApi.RakPeerInterface");
                if (api == null)
                {
                    _reflectError = "Brutal.RakNetApi.RakPeerInterface not found";
                    return false;
                }
                _mIsActive = AccessTools.Method(api, "IsActive");
                _mNumberOfConnections = AccessTools.Method(api, "NumberOfConnections");
                _mGetMaximumNumberOfPeers = AccessTools.Method(api, "GetMaximumNumberOfPeers");
                _mGetSystemAddressFromIndex = AccessTools.Method(api, "GetSystemAddressFromIndex");
                _mGetConnectionState = AccessTools.Method(api, "GetConnectionState");

                var addrType = AccessTools.TypeByName("Brutal.RakNetApi.SystemAddress");
                var aogType = AccessTools.TypeByName("Brutal.RakNetApi.AddressOrGUID");
                if (addrType != null && aogType != null)
                {
                    _mAddressFrom = AccessTools.Method(aogType, "From", new[] { addrType });
                    _mAddressToString = AccessTools.Method(addrType, "ToString", new[] { typeof(bool), typeof(char) });
                }

                _reflectOk = _mIsNull != null;
                if (!_reflectOk) _reflectError = "RakPeerInstance.IsNull not found";
                return _reflectOk;
            }
            catch (Exception ex)
            {
                _reflectError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>
        ///     The managed callers above a hook, so a teardown can be attributed. Harmony replaces the
        ///     hooked frame itself with a dynamic method, but the frames above it are the real call
        ///     chain, which is the part that answers "who did this".
        /// </summary>
        internal static string Caller(int frames)
        {
            try
            {
                var st = new StackTrace(1, false);
                var parts = new List<string>();
                for (int i = 0; i < st.FrameCount && parts.Count < frames; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    string owner = m.DeclaringType == null ? "?" : m.DeclaringType.Name;
                    parts.Add(owner + "." + m.Name);
                }
                return string.Join(" <- ", parts.ToArray());
            }
            catch { return "(stack unavailable)"; }
        }
    }
}
