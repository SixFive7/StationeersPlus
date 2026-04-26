# Maintenance Bureau Plus

A server-side bureau listens to every public chat message on your save. What the bureau does and who answers is for you to find out.

Full multiplayer compatibility. Safe to remove from existing savegames.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

> **WARNING:** This mod is work-in-progress under `Plans/`. Not released, not tagged, not on the Workshop. The README below describes the intended v1 shape, not a shipping product.

## Installation

1. Install [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) on the **server**.
2. Copy `MaintenanceBureauPlus.dll`, the `About/` folder, and all dependency DLLs into your Stationeers local mods directory on the server.
3. Download the GGUF model (see "Model file" below) and place it in a `Models/` folder next to `MaintenanceBureauPlus.dll`.
4. Restart the server.

### Model file

**The model file is NOT included in the repository.** At ~1 GB it exceeds GitHub's free LFS quota, so it is gitignored on purpose. Download it yourself.

- **File:** `qwen2.5-1.5b-instruct-q4_k_m.gguf` (~1065 MB)
- **Source:** [Qwen/Qwen2.5-1.5B-Instruct-GGUF on Hugging Face](https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF)
- **Direct download:** https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf

Place the downloaded file at `mods/MaintenanceBureauPlus/Models/qwen2.5-1.5b-instruct-q4_k_m.gguf` next to the deployed DLL. The filename is hardcoded; if you want a different model, rename it to match.

## How it works

Read the chat. Be patient. Find out.

You will notice the bureau the first time it speaks. You will figure out what it wants from you the second time. You will decide whether you like it or not some time after that.

## Settings

The mod exposes only two knobs, both in the in-game mod settings panel under `Server - Bureau`. Everything else is hardcoded by design.

| Setting | Default | Description |
|---|---|---|
| Minimum Turns | 5 | Lower bound on conversational hoops per request cycle. |
| Maximum Turns | 15 | Upper bound on conversational hoops per request cycle. |

If you find either extreme painful, adjust the other end instead of reading the source code.

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad.

**Server-side only.** Clients do not need this mod installed; the bureau's messages appear as normal chat and state changes go through the regular server-authoritative sync.

**Dedicated servers** need BepInEx + StationeersLaunchPad + Maintenance Bureau Plus installed and a `Models/` folder with the GGUF model file next to the DLL.

**Other mods.** The bureau has opinions about your save state. Other mods that rely on slow-building damage or lingering state for gameplay flavor may find parts of their contribution unexpectedly reversed during a bureau cycle. If that matters to you, disable the bureau or run a different mod.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Changelog

Version history lives in `MaintenanceBureauPlus/About/About.xml` under `<ChangeLog>` once the mod source tree is scaffolded. Workshop Change Notes will appear after the first publish.

## License

Apache License 2.0. See [LICENSE](../../LICENSE) for the full text and [NOTICE](../../NOTICE) for attribution.
