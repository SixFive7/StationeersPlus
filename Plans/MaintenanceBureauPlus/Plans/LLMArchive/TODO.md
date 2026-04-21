# LLM Mod - TODO

## Test regime

### Build verification

- [ ] Clean rebuild (`-t:Rebuild -p:Configuration=Release`) completes with zero errors, zero warnings
- [ ] `bin/Release/` contains `LLM.dll`, `LLamaSharp.dll`, and all `System.*` / `Microsoft.*` dependencies
- [ ] `bin/Release/runtimes/win-x64/native/` contains `llama.dll`, `ggml.dll`, `llava_shared.dll`
- [ ] `bin/Release/About/About.xml` is present and copies correctly

### Deployment (local dev)

- [ ] Copy `LLM.dll` + all dependency DLLs + `runtimes/` folder + `About/` + `models/` to the local Stationeers mods deploy folder (see `DEV.md` / `DEV.md.template` for the path on your setup; the deploy target directory is named `LLM/` or the post-rename mod code name).
- [ ] Verify folder contains no `.pdb`, `.deps.json`, source files, or `RESEARCH.md`
- [ ] Add `<Local Enabled="true"><Path Value="..." /></Local>` entry to `modconfig.xml` if not present

### Startup (dedicated server, batch mode)

- [ ] Start dedicated server with LLM installed
- [ ] `LogOutput.log` shows `[Info   :   LLM] Loading model: ...` followed by `[Info   :   LLM] Model loaded. Applying chat patches.`
- [ ] No `[Error]` or `[Fatal]` entries from LLM in the log
- [ ] Model load time is reasonable (under 60 seconds on server hardware)
- [ ] Server memory usage increases by roughly 2-3 GB after model load (check task manager)

### Startup (hosted game, non-batch mode)

- [ ] Start a hosted multiplayer game with LLM installed
- [ ] Same log messages as batch mode, no errors
- [ ] Game does not freeze or hitch noticeably during model load (load happens on main thread during `OnAllModsLoaded`, may need to be moved to background if it blocks)

### Startup (missing model file)

- [ ] Remove or rename the GGUF file from `models/`
- [ ] Start server. Log should show three `[Error]` lines about missing model and recommended filename
- [ ] No crash, no `[Fatal]`, server continues to function normally without the bot
- [ ] Chat works normally without the bot intercepting anything

### Chat trigger (basic)

- [ ] Connect a client to the server
- [ ] Type `@sat hello` in chat
- [ ] Server log shows `[Info   :   LLM] [SATCOM] Processing request from <YourName>: hello`
- [ ] After a few seconds, server log shows `[Info   :   LLM] [SATCOM] Response ready: ...`
- [ ] Client sees a cyan chat message from "SATCOM" in the console/chat log
- [ ] Response text is coherent, in character, and not empty

### Chat trigger (prefix filtering)

- [ ] Type a normal chat message without `@sat` prefix (e.g., "hello everyone")
- [ ] No LLM processing logged, no bot response
- [ ] Type `@sat` with no message body (just the prefix)
- [ ] No LLM processing logged (empty message after prefix strip)

### Chat trigger (empty prefix config)

- [ ] Set `Trigger Prefix` to empty string in the BepInEx config
- [ ] Restart server
- [ ] Type any message in chat
- [ ] Bot responds to every message (no prefix filtering)

### Self-loop prevention

- [ ] Verify the bot does not respond to its own messages
- [ ] Check log: only player messages trigger inference, never "SATCOM" messages

### Multiple requests (queue behavior)

- [ ] Have two or more players send `@sat` messages within seconds of each other
- [ ] Both requests are logged as received
- [ ] Responses arrive sequentially (first-in, first-out), not interleaved
- [ ] Both players see both responses in chat

### Response quality

- [ ] Ask a Stationeers-relevant question: `@sat what gas mix do I need for plants`
- [ ] Response should be on-topic (atmospheric pressure, CO2, O2, etc.)
- [ ] Ask something out of scope: `@sat what is the capital of France`
- [ ] Response should either answer briefly or blame signal interference (per system prompt)
- [ ] Verify responses stay under two sentences (per system prompt default)
- [ ] No ChatML template tokens (`<|im_start|>`, `<|im_end|>`) appear in the chat output

### Performance

- [ ] During inference, the server simulation continues without visible lag
- [ ] Other players can move, interact, and chat while the bot is generating
- [ ] `Update()` drain loop does not cause frame drops (should be near-zero cost when queue is empty)

### Cleanup (OnDestroy)

- [ ] Stop the server
- [ ] No crash or hang during shutdown
- [ ] No orphaned `LLM-Inference` thread (check with process explorer if needed)

### Config changes

- [ ] Change `Bot Name` to something else (e.g., "ORBITAL"), restart, verify new name appears in chat
- [ ] Change `System Prompt` to a different personality, verify responses reflect the change
- [ ] Change `Temperature` to 0.1 (very predictable) and 1.5 (very creative), verify noticeable difference in output style
- [ ] Change `Max Tokens` to 16, verify responses are very short
- [ ] Change `Inference Threads` to 1 vs. 8, verify inference still works (speed may differ)

## Pre-release checklist

- [ ] **Choose a real mod name**. `LLM` is a placeholder display name. Picking a real name is a cross-file rename:
  - `About/About.xml` `<Name>` (display name, with spaces) and `<ModID>` (`net.<newcodename>`)
  - `README.md` H1, tagline, WARNING box, and every inline reference to `LLM` / `LLM.dll`
  - `NOTICE` line 1 (code name)
  - Folder rename: `LLM/` → `<NewCodeName>/`, inner `LLM/` → `<NewCodeName>/`, `LLM.sln` → `<NewCodeName>.sln`, `LLM.csproj` → `<NewCodeName>.csproj`
  - `RootNamespace`, `AssemblyName`, `PluginGuid` / `PluginName` / namespace in `Plugin.cs`
  - The tagline "Adds a server-side chat companion..." stays the same and should continue to appear verbatim on GitHub, `<Description>` opener, and `<InGameDescription>` subtitle after the rename.
- [ ] Generate preview art (`Preview.source.png` at repo root, `About/Preview.png` at 1280x720, `About/thumb.png` at 640x360). Placeholder files are in place with dimension instructions; replace once source art exists.
- [x] Write `README.md`
- [x] Add `LICENSE` (Apache 2.0) and `NOTICE`
- [x] Sync `About.xml` `<Description>` and `<InGameDescription>` with README
- [x] Add Reporting Issues section to README and About.xml
- [x] Document model download instructions. README now covers model file, quantization, and the `models/` placement requirement for source and deploy.
- [x] Decide: ship model inside Workshop upload or separate download. Separate download chosen; model file is gitignored in the monorepo (`*.gguf` pattern). Revisit if Git LFS becomes cost-effective (tracked in the root `TODO.md`).
- [ ] Test on a clean machine (no NuGet cache) to verify all dependencies ship correctly.
- [x] Confirm the exact Hugging Face source URL for `qwen2.5-1.5b-instruct-q4_k_m.gguf`. README now links both the repo page (`huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF`) and the direct `resolve/main/...` download URL.
