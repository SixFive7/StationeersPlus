# LLM

![LLM](LLM/About/Preview.png)

Adds a server-side chat companion powered by a local language model that responds as a satellite communications relay.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

Players type a message in chat starting with a trigger word (default `@sat`). The server runs the message through a local language model and posts the response back under a bot name (default `SATCOM`). Responses take a few seconds depending on server hardware. The lore framing is a satellite relay with intermittent signal, so latency is a feature.

## Installation

1. Copy `LLM.dll` and the `About/` folder into your Stationeers local mods directory on the **server**
2. Download the GGUF model (see "Model file" below) and place it in a `models/` folder next to `LLM.dll`
3. Restart the server

### Model file

The model file is intentionally not shipped with the source tree (~1 GB, too large for a standard git repository without LFS). Download it separately and place it at `LLM/models/qwen2.5-1.5b-instruct-q4_k_m.gguf` for source builds, or at `mods/LLM/models/qwen2.5-1.5b-instruct-q4_k_m.gguf` alongside the deployed `LLM.dll`.

Default model: `qwen2.5-1.5b-instruct-q4_k_m.gguf` (~1065 MB). Published by Qwen as part of the `Qwen2.5-1.5B-Instruct-GGUF` release on Hugging Face. Pick the `q4_k_m` quantization unless you want to experiment with a smaller or larger variant; the `Model File Name` setting lets you point the mod at any GGUF file in the `models/` folder.

A different 1.5B-class instruction-tuned GGUF model will work if you prefer; match the filename to the `Model File Name` setting.

## Building from source

The MSBuild target `CopyModelFiles` (see `LLM/LLM.csproj`) copies any `*.gguf` under `LLM/LLM/models/` into `bin/Release/models/` during Release builds. If the folder is empty, the build still succeeds, but the mod will fail to load the model at runtime and log an error. Place the GGUF file there before a Release deploy.

## Features

### Chat Companion
Chat messages starting with the trigger prefix are routed to a local language model running on the server. The model generates a response on the server CPU and the response is posted back to chat under the configured bot name.

### Satellite Relay Framing
The default system prompt positions the bot as a satellite communications relay with intermittent signal, which frames inference latency as in-character rather than as server lag.

### Runs on CPU, No GPU Required
Inference runs on server CPU threads via LLamaSharp. Expect 2-4 GB of additional RAM usage on the server. No GPU needed.

### Asynchronous Inference
Generation runs on a background thread so the main server thread is not blocked while tokens are produced.

### Settings

All settings are server-side and configurable via the mod settings panel.

| Setting | Default | Description |
|---|---|---|
| Model File Name | qwen2.5-1.5b-instruct-q4_k_m.gguf | GGUF file in the `models/` folder next to `LLM.dll` |
| Bot Name | SATCOM | Name shown in chat for the bot's messages |
| System Prompt | (satellite relay persona) | The bot's personality and behavior instructions |
| Trigger Prefix | @sat | Chat prefix that activates the bot |
| Max Tokens | (see config) | Response length cap |
| Temperature | (see config) | Creativity vs. predictability |
| Inference Threads | (see config) | CPU threads for generation |
| Context Size | (see config) | Token window size |

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad

**Server-side only.** Clients do not need this mod installed; the bot's messages appear as normal chat.

**Dedicated servers** need BepInEx + StationeersLaunchPad + LLM installed and a `models/` folder with the GGUF model file next to `LLM.dll`.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Changelog

Version history lives in [`LLM/About/About.xml`](LLM/About/About.xml) under `<ChangeLog>`. Once the mod is published to the Steam Workshop, entries will also appear on the Workshop Change Notes tab with every release.

## License

Apache License 2.0. See [LICENSE](../../LICENSE) for the full text and [NOTICE](../../NOTICE) for attribution.
