using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using UnityEngine;

namespace PowerGridPlus
{
    // Per-Transformer MonoBehaviour that lights up the on/off button with an
    // orange pulse while the transformer is in the shed state. Mirrors
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
        private static readonly Color FlashColor = new Color(1f, 0.55f, 0f);   // #ffa500 orange
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorId = Shader.PropertyToID("_Color");

        // Set to true to dump renderer/material state into the log on every
        // shed-state transition (entry and exit). Leave false in committed code;
        // flip via reflection (`BrownoutFlashBehaviour.DiagnosticEnabled = true`)
        // from a probe scenario when triaging a paint mismatch.
        internal static bool DiagnosticEnabled = false;

        private Transformer _transformer;
        private MeshRenderer[] _renderers;
        private Color[] _originalEmissionColors;
        private bool[] _hadEmissionKeyword;
        private bool _wasFlashing;
        private bool _diagnosticLogged;
        private bool _transitionLogged;

        internal void Init(Transformer transformer)
        {
            _transformer = transformer;
            _renderers = DiscoverRenderers(transformer);
            CacheBaseline();
            LogDiagnostic();
        }

        private static MeshRenderer[] DiscoverRenderers(Transformer transformer)
        {
            if (transformer == null) return new MeshRenderer[0];

            // (1) On/Off Interactable -> its descendant MeshRenderers. This is the precise
            // path for the on/off button geometry. Interactable is `[Serializable]` (not a
            // MonoBehaviour), but it carries an `Animator` reference plus the GameObject
            // path via `BaseRotation` / `Targets`. Walk Transformer.Interactables and try
            // to resolve a Transform via reflection over the public field set.
            if (transformer.Interactables != null)
            {
                for (int i = 0; i < transformer.Interactables.Count; i++)
                {
                    var inter = transformer.Interactables[i];
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

            // (2) Name-substring fallback for prefab variants whose Interactable doesn't
            // have its own renderer subtree.
            var all = transformer.GetComponentsInChildren<MeshRenderer>();
            var preferred = System.Array.FindAll(all, r =>
            {
                var n = r != null && r.gameObject != null ? r.gameObject.name : null;
                if (string.IsNullOrEmpty(n)) return false;
                return n.IndexOf("OnOff", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Button", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Switch", System.StringComparison.OrdinalIgnoreCase) >= 0;
            });
            return preferred;
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
                Plugin.Log?.LogWarning($"BrownoutFlashBehaviour on Transformer ref={_transformer?.ReferenceId} prefab={_transformer?.PrefabName}: no MeshRenderer discovered for on/off button. Flash will not be visible. Hierarchy may need explicit InteractableType.OnOff Interactable or a renderer with Button/Switch/OnOff in its name.");
                return;
            }
            var names = string.Join(", ", _renderers.Where(r => r != null).Select(r => r.gameObject.name));
            Plugin.Log?.LogDebug($"BrownoutFlashBehaviour on Transformer ref={_transformer?.ReferenceId} prefab={_transformer?.PrefabName}: discovered {_renderers.Length} renderer(s) -> {names}");
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
            if (_transformer == null || _renderers == null || _renderers.Length == 0) return;

            int tick = ElectricityTickCounter.CurrentTick;
            bool shedding = ShedSettingsSync.Effective
                && BrownoutRegistry.IsShedding(_transformer.ReferenceId, tick);

            if (DiagnosticEnabled && shedding && !_transitionLogged)
            {
                _transitionLogged = true;
                DumpDiagnostic("ENTER_SHED");
            }
            else if (DiagnosticEnabled && !shedding && _transitionLogged)
            {
                _transitionLogged = false;
                DumpDiagnostic("EXIT_SHED");
            }

            if (!shedding)
            {
                if (_wasFlashing) RestoreBaseline();
                return;
            }

            // Lazy-initialise the runtime orange material clones on first shed.
            EnsureFlashMaterials();

            // Sinusoidal pulse at 2 Hz between black and FlashColor. Multiplied by 2 so the
            // peak is well past linear (the Standard shader stops emitting at intensity = 1
            // for some material configurations; pushing past 1 keeps the orange visible
            // through the gamma transform).
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * FlashHz * Mathf.PI * 2f);
            Color emission = FlashColor * (pulse * 2f);
            Color baseTint = Color.Lerp(Color.white, FlashColor, 0.4f * pulse);

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

        private void OnDestroy()
        {
            if (_wasFlashing) RestoreBaseline();
        }

        private void DumpDiagnostic(string transition)
        {
            try
            {
                int refId = _transformer != null ? (int)_transformer.ReferenceId : -1;
                bool onOff = _transformer != null && _transformer.OnOff;
                int error = _transformer != null ? _transformer.Error : -1;
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
