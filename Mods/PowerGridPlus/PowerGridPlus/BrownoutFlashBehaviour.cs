using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;
using VanillaSwitchOnOff = global::Objects.SwitchOnOff;

namespace PowerGridPlus
{
    // Per-device MonoBehaviour that lights up the on/off button with a colour
    // pulse while the device is in a fault state (POWER.md §11.4: attaches to
    // every segmenting device with an OnOff button and to every button-bearing
    // producer; FlashAttachPatches drives the attachment). Mirrors
    // LogicOnOffButton's flash behaviour without requiring shipped material
    // assets or prefab modification (Research/GameClasses/LogicOnOffButton.md).
    //
    // Renderer targeting (in priority order, first match wins):
    //   1. Every MeshRenderer that is a descendant of the InteractableType.OnOff
    //      Interactable on the Transformer. This is the on/off button's geometry.
    //   2. (Fallback) Every MeshRenderer whose GameObject name contains "OnOff"
    //      or "Button" or "Switch".
    //   3. No fallback to every child renderer: tinting the cabinet body produced
    //      a soft greenish wash on Luna saves where the existing powered LED
    //      colour was green; targeted matching is mandatory.
    //
    // Color application: per-renderer instance material with `_EMISSION` keyword
    // enabled and `_EmissionColor` set to the pulsing orange. Using a
    // MaterialPropertyBlock alone with `_EmissionColor` does not show because
    // most Stationeers materials are built with `_EMISSION` disabled, and a
    // property block cannot enable shader keywords. Using `Renderer.material`
    // forces Unity to instantiate a per-renderer copy, which we cache so the
    // shared base material is not mutated.
    //
    // Multiplayer: state is read from BrownoutRegistry, which is host-authoritative
    // and replicated to clients via ShedStateMessage. The client's Update reads
    // BrownoutRegistry.IsShedding which falls back to the client-replicated
    // dictionary on non-server peers.
    public class BrownoutFlashBehaviour : MonoBehaviour
    {
        private const float FlashHz = 2f;
        // Failure-colour split (POWER.md §11): SHED flashes orange (upstream undersupply),
        // every other fault (OVERLOAD, CYCLE_FAULT, VARIABLE_VOLTAGE_FAULT) flashes red. The
        // highest-precedence active fault picks the colour (CYCLE > VVF > OVERLOAD > SHED), and
        // because all non-shed faults are red the resolver only needs a red-vs-orange decision.
        internal static readonly Color OrangeFlashColor = new Color(1f, 0.55f, 0f);   // #ffa500 shed
        internal static readonly Color RedFlashColor = new Color(1f, 0.15f, 0.15f);   // #ff2626 overload/cycle/vvf
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorId = Shader.PropertyToID("_Color");

        // Set to true to dump renderer/material state into the log on every
        // shed-state transition (entry and exit). Leave false in committed code;
        // flip via reflection (`BrownoutFlashBehaviour.DiagnosticEnabled = true`)
        // from a probe scenario when triaging a paint mismatch.
        internal static bool DiagnosticEnabled = false;

        private Thing _device;
        private long _faultRefId;
        private MeshRenderer[] _renderers;
        private Color[] _originalEmissionColors;
        private bool[] _hadEmissionKeyword;
        private bool _wasFlashing;
        private bool _diagnosticLogged;
        private bool _transitionLogged;

        internal void Init(Thing device)
        {
            _device = device;
            // PR lockouts are keyed on the linked PT (the pair anchor); everything else keys itself.
            _faultRefId = FaultHover.ResolveFaultRefId(device);
            _renderers = DiscoverRenderers(device);
            CacheBaseline();
            LogDiagnostic();
        }

        private static MeshRenderer[] DiscoverRenderers(Thing device)
        {
            if (device == null) return new MeshRenderer[0];

            // (1) On/Off Interactable -> its descendant MeshRenderers. This is the precise
            // path for the on/off button geometry. Interactable is `[Serializable]` (not a
            // MonoBehaviour), but it carries an `Animator` reference plus the GameObject
            // path via `BaseRotation` / `Targets`. Walk Transformer.Interactables and try
            // to resolve a Transform via reflection over the public field set.
            if (device.Interactables != null)
            {
                for (int i = 0; i < device.Interactables.Count; i++)
                {
                    var inter = device.Interactables[i];
                    if (inter == null || inter.Action != InteractableType.OnOff) continue;
                    // The Interactable's `Animator` member's gameObject is the button visual root.
                    var animField = typeof(Interactable).GetField("Animator");
                    var anim = animField?.GetValue(inter) as Animator;
                    if (anim != null && anim.gameObject != null)
                    {
                        var childRenderers = anim.gameObject.GetComponentsInChildren<MeshRenderer>();
                        if (childRenderers.Length > 0) return childRenderers;
                    }
                }
            }

            // (2) Name-substring fallback for prefab variants whose Interactable doesn't have its own
            // renderer subtree. Covers the switch/lever/indicator geometry the player reads as the
            // device's state: the APC's MasterLever, the nuclear battery's Indic0NoShadow, plus the
            // generic on/off button / LED names. This branch is reached ONLY when (1) found nothing,
            // which excludes transformers (they resolve via the OnOff Interactable), so the green-LED
            // cabinet wash that motivated avoiding a broad tint cannot occur here.
            var all = device.GetComponentsInChildren<MeshRenderer>();
            string[] indicatorNames =
            {
                "OnOff", "Button", "Switch", "Lever", "Indic", "Led", "Light", "Lamp",
                "Display", "Screen", "Status", "Meter", "Glow", "Emissive"
            };
            var preferred = System.Array.FindAll(all, r =>
            {
                var n = r != null && r.gameObject != null ? r.gameObject.name : null;
                if (string.IsNullOrEmpty(n)) return false;
                for (int k = 0; k < indicatorNames.Length; k++)
                    if (n.IndexOf(indicatorNames[k], System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                return false;
            });
            if (preferred.Length > 0) return preferred;

            // (3) Last resort for a device with no dedicated indicator (a stationary battery exposes
            // only its body + broken-state meshes): flash the primary body mesh so the fault is at
            // least visible. Prefer the active built-state body (named "BuildState..." or after the
            // prefab); skip the BrokenState meshes (inactive on an intact device). Reached only after
            // (1) and (2) both fail, i.e. Battery / APC-without-lever, never a transformer.
            var body = System.Array.Find(all, r =>
            {
                var n = r != null && r.gameObject != null ? r.gameObject.name : null;
                if (string.IsNullOrEmpty(n)) return false;
                if (n.IndexOf("Broken", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return n.IndexOf("BuildState", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || (device.PrefabName != null && n == device.PrefabName);
            });
            return body != null ? new[] { body } : new MeshRenderer[0];
        }

        private void CacheBaseline()
        {
            if (_renderers == null) return;
            _originalEmissionColors = new Color[_renderers.Length];
            _hadEmissionKeyword = new bool[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] == null) continue;
                // Reading .material here intentionally instantiates the per-renderer copy
                // (Unity's documented behavior). The cached baseline lets us restore the
                // emission color on shed-exit; the instantiated material is harmless.
                var mat = _renderers[i].material;
                if (mat == null) continue;
                _originalEmissionColors[i] = mat.HasProperty(EmissionColorId)
                    ? mat.GetColor(EmissionColorId) : Color.black;
                _hadEmissionKeyword[i] = mat.IsKeywordEnabled("_EMISSION");
            }
        }

        private void LogDiagnostic()
        {
            if (_diagnosticLogged) return;
            _diagnosticLogged = true;
            if (_renderers == null || _renderers.Length == 0)
            {
                Plugin.Log?.LogWarning($"BrownoutFlashBehaviour on {_device?.GetType().Name} ref={_device?.ReferenceId} prefab={_device?.PrefabName}: no MeshRenderer discovered for on/off button. Flash will not be visible. Hierarchy may need explicit InteractableType.OnOff Interactable or a renderer with Button/Switch/OnOff in its name.");
                return;
            }
            var names = string.Join(", ", _renderers.Where(r => r != null).Select(r => r.gameObject.name));
            Plugin.Log?.LogDebug($"BrownoutFlashBehaviour on {_device?.GetType().Name} ref={_device?.ReferenceId} prefab={_device?.PrefabName}: discovered {_renderers.Length} renderer(s) -> {names}");
        }

        // LateUpdate (not Update) so the flash paint runs AFTER LogicOnOffButton's
        // state-driven material change. Without this, the OnOff state machine
        // overwrites our per-frame MPB write and the LED stays at its state
        // colour (green for On, red for Off) regardless of shed state.
        private void LateUpdate()
        {
            UpdateBody();
        }

        private void UpdateBody()
        {
            if (_device == null || _renderers == null || _renderers.Length == 0) return;

            int tick = ElectricityTickCounter.CurrentTick;
            // Highest-precedence active fault picks the colour (CYCLE > VVF > OVERLOAD > SHED,
            // POWER.md §11.5): every non-shed fault is red, shed is orange. The hover text
            // distinguishes the precise cause for the player.
            var fault = FaultHover.ActiveFault(_faultRefId, tick);
            bool flashing = fault != FaultHover.Kind.None;
            Color faultColor = fault == FaultHover.Kind.Shed ? OrangeFlashColor : RedFlashColor;

            if (DiagnosticEnabled && flashing && !_transitionLogged)
            {
                _transitionLogged = true;
                DumpDiagnostic("ENTER_FLASH");
            }
            else if (DiagnosticEnabled && !flashing && _transitionLogged)
            {
                _transitionLogged = false;
                DumpDiagnostic("EXIT_FLASH");
            }

            if (!flashing)
            {
                if (_wasFlashing)
                {
                    RestoreBaseline();
                    // Force vanilla SwitchOnOff.RefreshColorState so the
                    // button's material reflects the CURRENT (OnOff, Powered,
                    // HasPowerState, Error) tuple, not the material we cached
                    // at Init time. Without this, a transformer that the
                    // player turned OFF during the shed shows the cached ON-
                    // state material after the shed clears -- visually
                    // inconsistent with OnOff=false.
                    ForceVanillaRefresh();
                }
                return;
            }

            // Lazy-initialise the runtime orange material clones on first shed.
            EnsureFlashMaterials();

            // Sinusoidal pulse at 2 Hz between black and FlashColor. Multiplied by 2 so the
            // peak is well past linear (the Standard shader stops emitting at intensity = 1
            // for some material configurations; pushing past 1 keeps the orange visible
            // through the gamma transform).
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * FlashHz * Mathf.PI * 2f);
            Color emission = faultColor * (pulse * 2f);
            Color baseTint = Color.Lerp(Color.white, faultColor, 0.4f * pulse);

            // Material-swap approach: writing per-property values to the vanilla
            // SwitchOnOff materials does not work because the `on` / `onPowered`
            // materials carry a baked `_EmissionMap` texture that drives the
            // green glow regardless of `_EmissionColor`. Instead, swap each
            // renderer's material to a runtime orange-emissive instance for the
            // duration of the shed. The companion patch in SwitchOnOffShedPatches
            // suppresses vanilla RefreshColorState while the parent transformer
            // is shedding, so this swap is not overwritten on state transition.
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                if (_originalSharedMats == null || _orangeFlashMats == null) continue;
                if (i >= _orangeFlashMats.Length || _orangeFlashMats[i] == null) continue;
                _orangeFlashMats[i].SetColor(EmissionColorId, emission);
                _orangeFlashMats[i].SetColor(BaseColorId, baseTint);
                if (!_orangeFlashMats[i].IsKeywordEnabled("_EMISSION"))
                    _orangeFlashMats[i].EnableKeyword("_EMISSION");
                if (r.sharedMaterial != _orangeFlashMats[i])
                {
                    r.sharedMaterial = _orangeFlashMats[i];
                }
            }
            _wasFlashing = true;
        }

        // Cached original sharedMaterials so we can restore them on shed exit.
        private Material[] _originalSharedMats;
        // Runtime orange-emissive Material instances created at Init; one per renderer.
        private Material[] _orangeFlashMats;

        private void EnsureFlashMaterials()
        {
            if (_renderers == null) return;
            if (_originalSharedMats == null) _originalSharedMats = new Material[_renderers.Length];
            if (_orangeFlashMats == null) _orangeFlashMats = new Material[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] == null) continue;
                if (_originalSharedMats[i] == null) _originalSharedMats[i] = _renderers[i].sharedMaterial;
                if (_orangeFlashMats[i] == null && _originalSharedMats[i] != null)
                {
                    var clone = new Material(_originalSharedMats[i]);
                    // Strip the baked emission map so our _EmissionColor write isn't
                    // multiplied by a green texture.
                    try { if (clone.HasProperty("_EmissionMap")) clone.SetTexture("_EmissionMap", null); } catch { }
                    clone.EnableKeyword("_EMISSION");
                    _orangeFlashMats[i] = clone;
                }
            }
        }

        private void RestoreBaseline()
        {
            if (_renderers == null) return;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                if (_originalSharedMats != null && i < _originalSharedMats.Length && _originalSharedMats[i] != null)
                {
                    r.sharedMaterial = _originalSharedMats[i];
                }
            }
            _wasFlashing = false;
        }

        // Cached reflection handle for vanilla SwitchOnOff.RefreshColorState
        // (private instance method, no parameters). Resolved once at type
        // load via AccessTools so a null lookup degrades silently.
        private static readonly MethodInfo RefreshColorStateMethod =
            AccessTools.Method(typeof(VanillaSwitchOnOff), "RefreshColorState");

        private void ForceVanillaRefresh()
        {
            if (_device == null || RefreshColorStateMethod == null) return;
            var switches = _device.GetComponentsInChildren<VanillaSwitchOnOff>();
            if (switches == null) return;
            for (int i = 0; i < switches.Length; i++)
            {
                var s = switches[i];
                if (s == null) continue;
                try { RefreshColorStateMethod.Invoke(s, null); }
                catch (System.Exception e) { Plugin.Log?.LogWarning($"[BFB] RefreshColorState invoke failed for ref={_device.ReferenceId}: {e.Message}"); }
            }
        }

        private void OnDestroy()
        {
            if (_wasFlashing) RestoreBaseline();
        }

        private void DumpDiagnostic(string transition)
        {
            try
            {
                long refId = _device != null ? _device.ReferenceId : -1;
                var dev = _device as Assets.Scripts.Objects.Pipes.Device;
                bool onOff = dev != null && dev.OnOff;
                int error = dev != null ? dev.Error : -1;
                Plugin.Log?.LogInfo($"[BFB-DIAG] {transition} ref={refId} OnOff={onOff} Error={error} renderers={_renderers.Length}");
                // Also walk the Switch hierarchy and dump every MonoBehaviour
                // so we can identify what's competing for the LED renderer.
                if (transition == "ENTER_SHED" && _renderers.Length > 0)
                {
                    DumpHierarchyComponents();
                }
                for (int i = 0; i < _renderers.Length; i++)
                {
                    var r = _renderers[i];
                    if (r == null) { Plugin.Log?.LogInfo($"[BFB-DIAG]   [{i}] <null>"); continue; }
                    var go = r.gameObject;
                    string path = BuildHierarchyPath(go != null ? go.transform : null);
                    var mat = r.sharedMaterial;
                    string matName = mat?.name ?? "<null>";
                    bool hasEmKw = mat != null && mat.IsKeywordEnabled("_EMISSION");
                    Color emColor = (mat != null && mat.HasProperty(EmissionColorId)) ? mat.GetColor(EmissionColorId) : Color.magenta;
                    Color baseColor = (mat != null && mat.HasProperty(BaseColorId)) ? mat.GetColor(BaseColorId) : Color.magenta;
                    Plugin.Log?.LogInfo($"[BFB-DIAG]   [{i}] name={go?.name} path={path} mat={matName} _EMISSION={hasEmKw} _EmissionColor=({emColor.r:F2},{emColor.g:F2},{emColor.b:F2}) _Color=({baseColor.r:F2},{baseColor.g:F2},{baseColor.b:F2})");
                    DumpShaderProperties(mat, i);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning($"[BFB-DIAG] dump threw: {e.Message}");
            }
        }

        private static string BuildHierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder(t.name);
            var cur = t.parent;
            int hops = 0;
            while (cur != null && hops < 6)
            {
                sb.Insert(0, cur.name + "/");
                cur = cur.parent;
                hops++;
            }
            return sb.ToString();
        }

        private void DumpHierarchyComponents()
        {
            try
            {
                // Walk up to the SwitchOnOff root and dump every component on
                // every GameObject from there down.
                Transform root = null;
                for (int i = 0; i < _renderers.Length; i++)
                {
                    if (_renderers[i] == null) continue;
                    var t = _renderers[i].transform;
                    while (t != null && t.parent != null && !string.Equals(t.name, "SwitchOnOff", System.StringComparison.OrdinalIgnoreCase))
                        t = t.parent;
                    if (string.Equals(t?.name, "SwitchOnOff", System.StringComparison.OrdinalIgnoreCase))
                    { root = t; break; }
                }
                if (root == null && _renderers[0] != null) root = _renderers[0].transform;
                if (root == null) return;

                Plugin.Log?.LogInfo($"[BFB-HIER] root path={BuildHierarchyPath(root)}");
                WalkAndDump(root, 0);
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning($"[BFB-HIER] dump threw: {e.Message}");
            }
        }

        private static void WalkAndDump(Transform t, int depth)
        {
            if (t == null || depth > 4) return;
            string indent = new string(' ', depth * 2);
            var comps = t.GetComponents<Component>();
            var compNames = string.Join(", ", System.Linq.Enumerable.Select(comps, c => c == null ? "<null>" : c.GetType().Name));
            // Identify renderer state + animator if present.
            var mr = t.GetComponent<MeshRenderer>();
            string mpbInfo = "no-MR";
            if (mr != null)
            {
                var mpbProbe = new MaterialPropertyBlock();
                mr.GetPropertyBlock(mpbProbe);
                bool isEmpty = mpbProbe.isEmpty;
                mpbInfo = $"MR mat={mr.sharedMaterial?.name ?? "<null>"} mpbEmpty={isEmpty}";
            }
            var anim = t.GetComponent<Animator>();
            string animInfo = anim != null ? $"Animator runtimeController={anim.runtimeAnimatorController?.name ?? "<null>"}" : "no-Animator";
            Plugin.Log?.LogInfo($"[BFB-HIER]{indent}{t.name} components=[{compNames}] {mpbInfo} {animInfo}");
            for (int i = 0; i < t.childCount; i++)
            {
                WalkAndDump(t.GetChild(i), depth + 1);
            }
        }

        private static void DumpShaderProperties(Material mat, int rendererIdx)
        {
            try
            {
                var shader = mat?.shader;
                if (shader == null)
                {
                    Plugin.Log?.LogInfo($"[BFB-DIAG]   [{rendererIdx}] shader=<null>");
                    return;
                }
                Plugin.Log?.LogInfo($"[BFB-DIAG]   [{rendererIdx}] shader.name={shader.name} propertyCount={shader.GetPropertyCount()}");
                int n = shader.GetPropertyCount();
                for (int k = 0; k < n; k++)
                {
                    string pname = shader.GetPropertyName(k);
                    var ptype = shader.GetPropertyType(k);
                    string detail = "";
                    if (ptype == UnityEngine.Rendering.ShaderPropertyType.Color && mat.HasProperty(pname))
                    {
                        var c = mat.GetColor(pname);
                        detail = $" col=({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})";
                    }
                    else if (ptype == UnityEngine.Rendering.ShaderPropertyType.Float || ptype == UnityEngine.Rendering.ShaderPropertyType.Range)
                    {
                        detail = $" val={mat.GetFloat(pname):F3}";
                    }
                    Plugin.Log?.LogInfo($"[BFB-DIAG]   [{rendererIdx}]   prop[{k}]={pname} type={ptype}{detail}");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning($"[BFB-DIAG] DumpShaderProperties threw: {e.Message}");
            }
        }
    }
}
