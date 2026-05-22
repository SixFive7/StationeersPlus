using Assets.Scripts.Objects.Electrical;
using System.Collections.Generic;
using UnityEngine;

namespace PowerTransmitterPlus
{
    // Public surface: ReevaluateVisibility, SetLineIntensity,
    // InvalidateAllBeams. All are safe from any thread; show / hide work is
    // enqueued onto the main-thread dispatcher.
    //
    // Beam visibility is computed by BeamVisibility.ShouldShow(tx) from three
    // event-driven triggers, each firing on every peer (server, single-player
    // host, remote clients):
    //   - Link reference changes (LinkVisibilityPatch on the LinkedReceiver
    //     setter; fires on the server via TryContactReceiver / OnDestroy and
    //     on clients via ProcessUpdate).
    //   - On / off switch flips on either dish (OnOffPatches on
    //     Thing.OnInteractableUpdated filtered to Action == OnOff; fires on
    //     server, single-player, and remote clients via the interactable-
    //     state replication path).
    //   - Slew steps on either dish (RotationPatches on the
    //     WirelessPower.Horizontal / Vertical setters; fires per slew step
    //     on every peer that simulates the slew).
    // Steady-state cost is zero: idle, on, aimed dishes call none of these.
    //
    // The diagnostic log inside ReevaluateVisibility fires on the trigger
    // thread (main thread, server-included), BEFORE the dispatch-to-main, so
    // it runs on a headless dedicated server too, where the dispatcher's
    // Update would not pump. This lets server-side log inspection verify the
    // predicate behaviour without a rendering client. The actual Show / Hide
    // work runs after the dispatch and therefore only on peers with an
    // active main-thread Update (clients).
    //
    // The pulse train (the scrolling stripes) is driven separately by
    // SetLineIntensity off the WirelessPower.VisualizerIntensity setter
    // (VisualiserPatches). Stripes scroll when power flows, freeze in place
    // when the link is up but power is zero. SetLineIntensity does NOT drive
    // show / hide.
    internal static class BeamManager
    {
        private static readonly Dictionary<PowerTransmitter, BeamLine> Beams =
            new Dictionary<PowerTransmitter, BeamLine>();

        private static Material _sharedMaterial;
        private static Texture2D _stripeTexture;

        // Procedural 1D stripe texture for the pulse train. Repeat-wrapped so
        // LineRenderer UV tiling via mainTextureScale.x gives one cosine period
        // per stripe. Brightness floor = StripeTroughBrightness; peak = 1.0.
        // Regenerated lazily on first access; tied to current config value.
        internal static Texture2D StripeTexture
        {
            get
            {
                if (_stripeTexture != null) return _stripeTexture;

                const int width = 32;
                var tex = new Texture2D(width, 1, TextureFormat.RGBA32, mipChain: false)
                {
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var trough = Mathf.Clamp01(BeamVisualConfigSync.GetEffectiveStripeTroughBrightness());
                for (int i = 0; i < width; i++)
                {
                    float t = i / (float)width;
                    // 0.5 + 0.5*cos(2*pi*t) = 1 at t=0, 0 at t=0.5, 1 at t=1
                    float wave = 0.5f + 0.5f * Mathf.Cos(t * 2f * Mathf.PI);
                    float b = Mathf.Lerp(trough, 1f, wave);
                    tex.SetPixel(i, 0, new Color(b, b, b, 1f));
                }
                tex.Apply();
                _stripeTexture = tex;
                return _stripeTexture;
            }
        }

        internal static Material SharedMaterial
        {
            get
            {
                if (_sharedMaterial != null) return _sharedMaterial;

                var shader = Shader.Find("Legacy Shaders/Particles/Additive")
                             ?? Shader.Find("Particles/Additive")
                             ?? Shader.Find("Sprites/Default")
                             ?? Shader.Find("Hidden/Internal-Colored");
                _sharedMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return _sharedMaterial;
            }
        }

        // Beam RGB at full intensity. Uses effective values (synced from host
        // when Enforce Visual Sync is on, local config otherwise).
        internal static Color BeamColor
        {
            get
            {
                var hex = BeamVisualConfigSync.GetEffectiveBeamColorHex();
                if (!ColorUtility.TryParseHtmlString("#" + hex, out var c)) c = new Color(0f, 0.049f, 1f, 1f);

                var boost = BeamVisualConfigSync.GetEffectiveEmissionIntensity();
                if (boost > 0f && boost != 1f)
                {
                    c = new Color(c.r * boost, c.g * boost, c.b * boost, c.a);
                }
                return c;
            }
        }

        // Force all existing beams to be destroyed so they are recreated with
        // current visual settings on the next ReevaluateVisibility that
        // returns shouldShow == true.
        internal static void InvalidateAllBeams()
        {
            MainThreadDispatcher.Enqueue(InvalidateAllBeamsOnMain);
        }

        private static void InvalidateAllBeamsOnMain()
        {
            foreach (var beam in Beams.Values)
            {
                if (beam != null && !beam.IsDestroyed) beam.Destroy();
            }
            Beams.Clear();

            // Trough Brightness is baked into the stripe texture at creation,
            // so drop the cache here and let the next beam rebuild it from
            // the current (possibly just-synced) value.
            if (_stripeTexture != null)
            {
                UnityEngine.Object.Destroy(_stripeTexture);
                _stripeTexture = null;
            }
        }

        // Re-run BeamVisibility.ShouldShow(transmitter) and show / hide
        // accordingly, refreshing endpoints when shown. Called from every
        // event source that can change a predicate input (link reference,
        // either dish's OnOff, either dish's current orientation).
        //
        // The diagnostic log fires immediately on the trigger thread (main
        // thread; see class-level comment), BEFORE the dispatch-to-main, so
        // server-side log inspection sees every evaluation on a headless
        // dedicated server even though the dispatched Show / Hide work runs
        // only where the main-thread Update pumps.
        //
        // `context` is a short label ("Link", "OnOff", "Slew", ...) carried
        // into the diagnostic log so analysis can grep per trigger source.
        internal static void ReevaluateVisibility(PowerTransmitter transmitter, string context = "Generic")
        {
            if (transmitter == null) return;

            if (PowerTransmitterPlusPlugin.BeamDiagnosticLogging != null
                && PowerTransmitterPlusPlugin.BeamDiagnosticLogging.Value)
            {
                PowerTransmitterPlusPlugin.Log?.LogInfo(
                    $"[BeamDiagnostic][{context}] {BeamVisibility.Describe(transmitter)}");
            }

            MainThreadDispatcher.Enqueue(() => ReevaluateVisibilityOnMain(transmitter));
        }

        // Pulse-speed signal. VisualizerIntensity (0..1) drives the pulse
        // train scroll speed; intensity == 0 freezes the stripes. Does NOT
        // drive show / hide.
        internal static void SetLineIntensity(PowerTransmitter transmitter, float intensity)
        {
            if (transmitter == null) return;
            MainThreadDispatcher.Enqueue(() => SetLineIntensityOnMain(transmitter, intensity));
        }

        private static void ReevaluateVisibilityOnMain(PowerTransmitter transmitter)
        {
            if (transmitter == null) return;

            if (!BeamVisibility.ShouldShow(transmitter))
            {
                HideBeamOnMain(transmitter);
                return;
            }

            // Predicate said show. ShouldShow already verified LinkedReceiver
            // and both RayTransforms are non-null, but the state could have
            // changed between trigger and dispatch (one-frame window); the
            // re-check below is defensive and cheap.
            var receiver = transmitter.LinkedReceiver;
            if (receiver == null || transmitter.RayTransform == null || receiver.RayTransform == null)
            {
                HideBeamOnMain(transmitter);
                return;
            }

            if (!Beams.TryGetValue(transmitter, out var beam) || beam == null || beam.IsDestroyed)
            {
                beam = new BeamLine(transmitter);
                Beams[transmitter] = beam;
            }

            if (!beam.IsVisible) beam.Show();
            else beam.Refresh();
        }

        private static void HideBeamOnMain(PowerTransmitter transmitter)
        {
            if (Beams.TryGetValue(transmitter, out var existing) && existing != null && !existing.IsDestroyed)
                existing.Hide();
        }

        private static void SetLineIntensityOnMain(PowerTransmitter transmitter, float intensity)
        {
            if (transmitter == null) return;
            if (Beams.TryGetValue(transmitter, out var beam) && beam != null && !beam.IsDestroyed)
                beam.SetIntensity(intensity);
        }
    }
}
