---
title: UnregisteredSaveDataBehavior
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.XmlSerialization.Deserialize(XmlSerializer, StreamReader)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.XmlSaveLoad.LoadWorld
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.ThingSaveData
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Util.Serializers.WorldData property
related:
  - ../GameSystems/SaveDataRegistration.md
tags: [save-load]
---

# UnregisteredSaveDataBehavior

Runtime behavior when a save file contains a `ThingSaveData`-derived type (e.g., `GlowThingSaveData`) whose class is NOT registered in `XmlSaveLoad.ExtraTypes`. This is the "mod removed but its save data still in file" scenario.

## Summary

**The entire save fails to load.** There is no per-Thing fallback, no substitution of base class, and no silent skip. When `XmlSerializer` encounters an `xsi:type` attribute referencing an unknown type during `WorldData` deserialization, it throws `InvalidOperationException`. This exception is caught and logged, the deserialization returns `null`, and the game displays "Failed to load the world.xml" to the player.

## XmlSerializer behavior on unknown xsi:type
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

When a save file contains polymorphic XML (e.g., `<ThingSaveData xsi:type="GlowThingSaveData">...</ThingSaveData>`) and the `XmlSerializer` is constructed with an `ExtraTypes` array that does NOT include `GlowThingSaveData`, the .NET Framework's `XmlSerializer.Deserialize()` method throws `InvalidOperationException` with a message like:

```
The type [namespace].GlowThingSaveData was not expected. Use the XmlInclude or SoapInclude attribute to specify types that are not known statically.
```

This is standard .NET behavior for polymorphic deserialization with unknown derived types.

## Polymorphic encoding in the XML
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The `WorldData` class in `Assets.Scripts.Serialization.XmlSaveLoad` is decorated:

```csharp
[XmlInclude(typeof(ThingSaveData))]
[XmlRoot("WorldData")]
public class WorldData
{
    [XmlArray("AllThings")]
    public List<ThingSaveData> OrderedThings;
    // ...
}
```

The `[XmlInclude]` on the containing class signals the serializer to accept `ThingSaveData` and its registered subclasses (via `XmlSaveLoad.ExtraTypes`) in the Things collection. When serializing, each derived type is marked with `xsi:type`:

```xml
<WorldData>
  <AllThings>
    <ThingSaveData xsi:type="GlowThingSaveData">
      <!-- fields -->
    </ThingSaveData>
  </AllThings>
</WorldData>
```

Upon deserialization, the `xsi:type` attribute is read and the corresponding class must exist in the `XmlSerializer`'s extraTypes array. If it doesn't, deserialization fails immediately at the class level, not at the element level.

## Game-level exception handling
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The deserialization call chain is:

1. **`XmlSaveLoad.LoadWorld()`** (line 855 in decompiled Assembly-CSharp.dll):
   ```csharp
   object obj = XmlSerialization.Deserialize(Serializers.WorldData, fullName);
   if (!(obj is WorldData worldData))
   {
       UpdateLoadingScreen(display: false);
       throw new NullReferenceException("Failed to load the world.xml: " + fullName);
   }
   ```

2. **`XmlSerialization.Deserialize()`** (line 103-119 in Assets.Scripts.Serialization.XmlSerialization):
   ```csharp
   public static object Deserialize(XmlSerializer xmlSerializer, StreamReader streamReader, string path = "")
   {
       if (xmlSerializer == null || streamReader == null)
       {
           streamReader?.Close();
           return null;
       }
       try
       {
           using XmlReader xmlReader = XmlReader.Create(streamReader, XmlSaveLoad.XmlReaderSettings);
           return xmlSerializer.Deserialize(xmlReader);
       }
       catch (Exception ex)
       {
           Debug.LogException(ex);
           Debug.LogError("An error occurred while deserializing a file!: " + path + " - " + ex.Message + 
               ((ex.InnerException != null) ? (" : " + ex.InnerException.Message) : ""));
           return null;  // <-- Returns null, not a partial object
       }
       finally
       {
           streamReader.Close();
       }
   }
   ```

The catch block captures ALL exceptions (including `InvalidOperationException`), logs them, and returns `null`. There is **no per-element try-catch**, and **no fallback to base class deserialization**. The entire `WorldData` deserialization fails.

The caller then checks the result:

```csharp
if (!(obj is WorldData worldData))
{
    UpdateLoadingScreen(display: false);
    throw new NullReferenceException("Failed to load the world.xml: " + fullName);
}
```

This exception propagates to the UI layer and is displayed as a load failure. The save cannot be opened.

## Per-Thing behavior: no fallback
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

There is no per-Thing deserialize pass that could recover from a missing type. The `LoadThing()` method (line 805 onwards) receives an already-deserialized `ThingSaveData` object. If deserialization failed earlier, this method is never called:

```csharp
public static Thing LoadThing(ThingSaveData thingData, bool generatesTerrain = true)
{
    Thing thing = Prefab.Find(thingData.PrefabName);
    if (thing == null) { /* ... */ return null; }
    if (!thingData.IsValidData()) { return null; }
    // ... creates and restores the Thing
    thing2.DeserializeSave(thingData);
    thing2.ValidateOnLoad(CurrentSaveRevision);
    return thing2;
}
```

If the `ThingSaveData` object doesn't exist due to deserialization failure, `LoadThing()` is never invoked for that item. The entire world load halts.

## User-visible consequence
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

When a save file contains an unregistered custom `ThingSaveData` subclass (e.g., `GlowThingSaveData` from a removed mod):

- **Save fails to load at all.** The player sees an error dialog: "Failed to load the world.xml: [path]"
- **No Things are restored.** The load sequence never reaches the per-Thing loop.
- **The save is effectively lost** until the custom type is re-registered via a mod (or manually removed from the XML via offline editing).

## XmlSerializer construction site
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The relevant `XmlSerializer` instances are cached in `Assets.Scripts.Util.Serializers`:

```csharp
public static class Serializers
{
    private static XmlSerializer _worldData;

    public static XmlSerializer WorldData
    {
        get
        {
            if (_worldData != null) { return _worldData; }
            _worldData = new XmlSerializer(typeof(XmlSaveLoad.WorldData), XmlSaveLoad.ExtraTypes);
            return _worldData;
        }
    }
}
```

The `XmlSaveLoad.ExtraTypes` array is populated at load time by `StationeersLaunchPad`'s mod loader. If a mod's registration is missing (mod uninstalled), its types are not in the array. When the cached serializer is created, it locks in the set of known types. If a mod is then removed and a save loaded without restarting, the old save's custom types are unknown.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- 2026-04-21: page created from direct decompilation of Assembly-CSharp.dll v0.2.6228.27061. All sections verified against verbatim decompiled source code.

## Open questions

None at creation.
