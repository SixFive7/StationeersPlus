# GlowPaintProbe

Throwaway probe plugin for the SprayPaintPlus glow-paint feature. Three open questions gate the design; this plan specifies a single BepInEx plugin that answers all three via BepInEx log output. InspectorPlus is off-limits for this session; every signal is emitted to `BepInEx/LogOutput.log` and read from there.

Delete this `Plans/` entry once the answers are recorded into `Research/` and the feature ships.

## Purpose

Three things need to be verified against a running game before the glow-paint implementation:

1. Which entries in `GameManager.CustomColors` ship with a non-null `Emissive` material. Per `Research/GameClasses/ColorSwatch.md`, `ColorSwatch.Emissive` is optional per swatch; vanilla contains `if (CustomColor.Emissive == null)` null-checks. If the common paint colors are all null, the "just call `SetCustomColor(index, emissive: true)`" plan does not swap the material; only the `_EmissionColor` write happens, and whether that alone produces visible glow depends on whether the `Normal` material's shader honors the property.
2. Whether a painted pipe glows visibly when `Thing.SetCustomColor(index, emissive: true)` is called. Behavioral test for approach F.1 in `Research/GameSystems/RenderingPipelineAndGlow.md`.
3. Whether UltimateBloom is on for the local camera by default. Without bloom, emission renders self-lit but does not halo. The Research page says it is on by default; this probe confirms on this developer's graphics settings.

The answers route directly into the feature design: they decide whether approach F.1 alone suffices, whether approach F.2 (per-instance `_EmissionColor` write) is needed as a fallback for null-`Emissive` swatches, and whether the feature requires a "bloom must be on" precondition documented to the player.

## Probe plugin behavior

One plugin answers all three questions via BepInEx log. Every probe log line is prefixed `[GlowPaintProbe]` for grep-ability.

### At game start (via `Prefab.OnPrefabsLoaded`)

The plugin emits:

1. One log line per entry in `GameManager.CustomColors`, in index order. This answers probe 1 in its entirety:

        [GlowPaintProbe] swatch index=NN name="..." normal=yes|no emissive=yes|no

   The developer greps for `[GlowPaintProbe] swatch` and reports the `emissive=yes|no` distribution, with special attention to the swatches that correspond to the paint-scroll colors players use most (red, blue, green, yellow, white, black).

2. One log line for the bloom state. This answers probe 3:

        [GlowPaintProbe] bloom present=yes|no enabled=yes|no component=<type name or null>

   If `CameraController.Instance` or its effect collection is not yet reachable on `OnPrefabsLoaded`, retry on each `Update` tick until it resolves, then emit once and set a `_bloomLogged` flag.

### On F9 key press

For the Thing the player is looking at:

1. Log the target:

        [GlowPaintProbe] target type=<Pipe|Wall|...> name="..." colorIndex=N colorName="..."

2. Call `target.SetCustomColor(target.CustomColor.Index, emissive: true)`.

3. Log each renderer's material state post-call. This answers probe 2 together with visual observation:

        [GlowPaintProbe] renderer[i] shader="..." _EmissionColor=(r,g,b,a) _EMISSION=on|off material="<name>"

### On F10 key press

Same as F9 but with `emissive: false`. Used to reset the pipe and validate the transience story from `Research/GameClasses/ColorSwatch.md`.

### If F9 or F10 is pressed with no target

Single log line and skip:

    [GlowPaintProbe] no look-at target

## Probe plugin spec (not yet implemented)

Minimal BepInEx plugin at `Plans/GlowPaintProbe/GlowPaintProbe/`. Sketch:

    [BepInPlugin("net.glowpaintprobe", "GlowPaintProbe", "0.1.0")]
    [BepInDependency("net.stationeerslaunchpad")]
    public class Plugin : BaseUnityPlugin
    {
        static ConfigEntry<KeyboardShortcut> GlowOnKey;
        static ConfigEntry<KeyboardShortcut> GlowOffKey;
        internal static ManualLogSource Log;
        bool _bloomLogged;

        void Awake()
        {
            Log = Logger;
            GlowOnKey  = Config.Bind("General", "Glow On Key",  new KeyboardShortcut(KeyCode.F9));
            GlowOffKey = Config.Bind("General", "Glow Off Key", new KeyboardShortcut(KeyCode.F10));
            Prefab.OnPrefabsLoaded += EnumerateSwatchesAndBloom;
        }

        void EnumerateSwatchesAndBloom()
        {
            for (int i = 0; i < GameManager.CustomColors.Count; i++)
            {
                var s = GameManager.CustomColors[i];
                Log.LogInfo($"[GlowPaintProbe] swatch index={i} name=\"{s.Name}\" " +
                            $"normal={(s.Normal != null ? "yes" : "no")} " +
                            $"emissive={(s.Emissive != null ? "yes" : "no")}");
            }
            TryLogBloom();
        }

        void Update()
        {
            if (!_bloomLogged) TryLogBloom();
            if (GlowOnKey.Value.IsDown())  ApplyGlow(true);
            if (GlowOffKey.Value.IsDown()) ApplyGlow(false);
        }

        void TryLogBloom()
        {
            var cam = CameraController.Instance;        // accessor to be pinned at impl time
            var bloom = cam?.Effects?.Bloom;            // accessor to be pinned at impl time
            if (cam == null) return;
            Log.LogInfo($"[GlowPaintProbe] bloom present={(bloom != null ? "yes" : "no")} " +
                        $"enabled={(bloom != null && bloom.enabled ? "yes" : "no")} " +
                        $"component={bloom?.GetType().Name ?? "null"}");
            _bloomLogged = true;
        }

        void ApplyGlow(bool emissive)
        {
            var target = FindLookAtThing();
            if (target == null) { Log.LogInfo("[GlowPaintProbe] no look-at target"); return; }
            if (target.CustomColor == null) {
                Log.LogInfo($"[GlowPaintProbe] target has no CustomColor: {target}"); return;
            }

            Log.LogInfo($"[GlowPaintProbe] target type={target.GetType().Name} " +
                        $"name=\"{target.DisplayName}\" colorIndex={target.CustomColor.Index} " +
                        $"colorName=\"{target.CustomColor.Name}\"");

            target.SetCustomColor(target.CustomColor.Index, emissive);

            for (int i = 0; i < target.Renderers.Count; i++)
            {
                var r = target.Renderers[i];
                var mat = r.GetMaterial();   // accessor to be pinned at impl time
                Log.LogInfo($"[GlowPaintProbe] renderer[{i}] " +
                            $"shader=\"{mat?.shader.name}\" " +
                            $"_EmissionColor={mat?.GetColor(\"_EmissionColor\")} " +
                            $"_EMISSION={(mat?.IsKeywordEnabled(\"_EMISSION\") == true ? "on" : "off")} " +
                            $"material=\"{mat?.name}\"");
            }
        }

        Thing FindLookAtThing()
        {
            // Pin at implementation time. Candidates, in order of preference:
            //  1. InventoryManager.ParentBrain.CurrentInteractable as Thing
            //  2. Cursor / reticle target accessor (name not yet known)
            //  3. Fallback: nearest Thing of known types within N meters of the camera,
            //     facing the camera.
            return null;
        }
    }

Three accessors are marked for resolution at implementation time: `CameraController.Instance.Effects.Bloom` (probably this shape, needs confirmation), `ThingRenderer.GetMaterial()` (exact name unknown; may be a field, property, or method), and `FindLookAtThing` (the game's cursor-target API). A short decomp pass settles all three.

Build and packaging follow `Mods/Template/` conventions: `GlowPaintProbe.csproj` referencing game assemblies via `$(StationeersPath)`, a minimal `About/About.xml` with `<WorkshopHandle>0</WorkshopHandle>`, and a copy target that drops the built DLL into `<StationeersInstall>/BepInEx/plugins/GlowPaintProbe/`.

## Test procedure

1. Build the plugin and deploy its DLL to the BepInEx plugins folder.
2. Launch the game. Load a save with a single test pipe placed in a dark room (no ceiling lights, door closed, other light sources removed or far away).
3. After the save finishes loading, tail `<StationeersInstall>/BepInEx/LogOutput.log` and grep for `[GlowPaintProbe]`.
4. Copy the swatch enumeration output (probe 1) and the bloom line (probe 3) from the log. These are the first two answers.
5. Paint the test pipe any color using a SprayCan.
6. Stand facing the pipe. Press F9. Observe the pipe visually.
7. Record the renderer log line (probe 2 evidence) and answer: did the pipe glow with a visible halo?
8. Press F10. Observe whether the pipe returns to its non-glowing appearance. Record the follow-up log line.

## Answers-to-design mapping

| Probe 1 outcome | Implication |
|---|---|
| All common paint colors have non-null `Emissive` | Approach F.1 suffices. |
| Some paint colors null | Approach F.1 primary, approach F.2 fallback for null-`Emissive` swatches. |
| None / few | Approach F.2 primary, `Emissive` material path abandoned. |

| Probe 2 outcome | Implication |
|---|---|
| Pipe glows with visible halo after F9 | Approach F.1 works. Proceed to implementation. |
| Self-lit, no halo | Bloom is off (cross-check probe 3). Document graphics requirement or force-enable. |
| No visible change AND log shows `_EmissionColor` updated but shader does not name `_EMISSION` | Normal material's shader does not honor `_EmissionColor`; approach F.2 requires material swap, not just property write. |
| Glow cleared by a second paint / color-scroll | Transience confirmed; the re-application postfix in the feature design is required. |

| Probe 3 outcome | Implication |
|---|---|
| Bloom present and enabled | No action. |
| Present but disabled | Document requirement in README; consider force-enable when glowing Things are in scene. |
| Absent on this preset | Stronger documentation; investigate why on this graphics preset. |

## Cleanup

1. Remove the plugin DLL from `<StationeersInstall>/BepInEx/plugins/GlowPaintProbe/`.
2. Record the answers in `Research/` by updating the "Open questions" sections in `Research/GameClasses/ColorSwatch.md` and `Research/GameSystems/RenderingPipelineAndGlow.md`. Use this citation form since there is no snapshot file to cite: "Verified via GlowPaintProbe plugin logs on YYYY-MM-DD in game version 0.2.6228.27061."
3. Delete `Plans/GlowPaintProbe/` once the three questions are answered and the findings are in `Research/`.
