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

        private Transformer _transformer;
        private MeshRenderer[] _renderers;
        private Color[] _originalEmissionColors;
        private bool[] _hadEmissionKeyword;
        private bool _wasFlashing;
        private bool _diagnosticLogged;

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

        private void Update()
        {
            if (_transformer == null || _renderers == null || _renderers.Length == 0) return;

            int tick = ElectricityTickCounter.CurrentTick;
            bool shedding = ShedSettingsSync.Effective
                && BrownoutRegistry.IsShedding(_transformer.ReferenceId, tick);

            if (!shedding)
            {
                if (_wasFlashing) RestoreBaseline();
                return;
            }

            // Sinusoidal pulse at 2 Hz between black and FlashColor. Multiplied by 2 so the
            // peak is well past linear (the Standard shader stops emitting at intensity = 1
            // for some material configurations; pushing past 1 keeps the orange visible
            // through the gamma transform).
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * FlashHz * Mathf.PI * 2f);
            Color emission = FlashColor * (pulse * 2f);

            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                var mat = r.material;
                if (mat == null) continue;
                if (!mat.IsKeywordEnabled("_EMISSION"))
                    mat.EnableKeyword("_EMISSION");
                if (mat.HasProperty(EmissionColorId)) mat.SetColor(EmissionColorId, emission);
                if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, Color.Lerp(Color.white, FlashColor, 0.4f * pulse));
            }
            _wasFlashing = true;
        }

        private void RestoreBaseline()
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                var mat = r.material;
                if (mat == null) continue;
                if (mat.HasProperty(EmissionColorId) && _originalEmissionColors != null && i < _originalEmissionColors.Length)
                    mat.SetColor(EmissionColorId, _originalEmissionColors[i]);
                if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, Color.white);
                if (_hadEmissionKeyword != null && i < _hadEmissionKeyword.Length && !_hadEmissionKeyword[i])
                    mat.DisableKeyword("_EMISSION");
            }
            _wasFlashing = false;
        }

        private void OnDestroy()
        {
            if (_wasFlashing) RestoreBaseline();
        }
    }
}
