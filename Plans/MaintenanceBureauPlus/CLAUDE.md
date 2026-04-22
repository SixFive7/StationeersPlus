# Maintenance Bureau Plus: Local Conventions

Supplements the monorepo root `CLAUDE.md`. Only rules specific to this mod live here. Every rule in the root `CLAUDE.md` still applies.

## Large model files are never committed

Any file that holds model weights or other large binary artifacts stays out of the repository. Do not bypass this via Git LFS without explicit sign-off and a corresponding root `TODO.md` entry.

Covered file types (must remain gitignored, repo-wide):

- `*.gguf` (llama.cpp / GGUF models; already caught by the root `.gitignore`)
- `*.bin` (raw weight dumps, older Hugging Face format)
- `*.safetensors` (modern Hugging Face weight format)
- `*.onnx` (ONNX runtime model files)
- `*.pt`, `*.pth` (PyTorch checkpoint files)
- `*.ckpt` (generic checkpoint files)
- `*.tflite` (TensorFlow Lite models)

Where model files actually live:

- Developer-local copy for source builds: `MaintenanceBureauPlus/Models/<filename>` inside the mod source folder (gitignored).
- End-user deploy: `mods/MaintenanceBureauPlus/Models/<filename>` next to `MaintenanceBureauPlus.dll` in the Stationeers mods directory.
- Archive / backup: outside the repo entirely. Canonical source URLs documented in `README.md`.

Why:

- Model weights routinely run 500 MB to multi-GB. GitHub caps a single non-LFS file at 100 MB and the free LFS tier at 1 GB storage plus 1 GB / month bandwidth. A single GGUF blows through that quota on the first commit.
- Every `git clone` would otherwise download the weights. Bandwidth cost compounds per contributor and per CI run.
- Model files are commodity artifacts with stable download URLs. `README.md` is the source of truth for where to fetch them.

When adding a new quantization or a different model format:

- Confirm its extension is already caught by a `.gitignore` pattern. If not, add one at the repo root alongside the existing `*.gguf` entry.
- Document the expected filename, size, and download URL under the "Model file" section of `README.md`.
- Do not commit a "just for testing" copy. History retention makes that permanent even if reverted.

Exception: if a genuinely small synthetic model fixture is needed for automated tests (under 10 MB, clearly not a real model), it may live under `MaintenanceBureauPlus/tests/fixtures/`. Anything larger needs the same discussion as a full model before committing.

## Archives under Plans/ are reference material, not scaffolding

The three folders under `Plans/MaintenanceBureauPlus/Plans/` carry content from the three original prototypes this mod collapses:

- `LLMArchive/` the chat-plus-LLamaSharp prototype that seeds v1 implementation.
- `RepairArchive/` the BCSI design document (43 KB) that seeded v1 lore and settings.
- `TerrainReclamation/` the SaveFix prototype, including the Python offline tool, held for v2.

Rules for these archives:

- Do not edit archive files to change the original prototypes' content. They are a snapshot. If you need to update a design decision that originated in an archive, update the archive's line-range pointer in `RESEARCH.md` and record the change there, not inline.
- It is fine to add new files alongside archived ones (e.g. `TerrainReclamation/NOTES-v2-reopen.md` when v2 work begins).
- When v2 terrain reclamation ships, `TerrainReclamation/` graduates out of `Plans/` into the mod's main source tree or into a separate `Plans/Terrain/v2.md` per the final layout chosen at the time.

## No abbreviations for this mod's name or its dependencies

Per the root `CLAUDE.md` rule, always use the full display name `Maintenance Bureau Plus` or the code name `MaintenanceBureauPlus`. Never invent an acronym (no `MBP`, no `MB+`, no `Bureau`-alone when writing committed docs). The same applies to dependencies: `StationeersLaunchPad`, `LaunchPadBooster`, `LLamaSharp`, `BepInEx`, all spelled out.

In conversational lore inside the mod itself (the officer's in-game chat replies, the player-facing system prompt), the bureau is free to abbreviate itself however it likes: "the Bureau," "MB7734-C," "the Office of Structural Auditing," are all flavor. That is dialogue, not committed doc prose. The root rule covers the latter; the former is creative content.

## Approval-tag format is part of the mod's public contract

The `[CONTINUE]` / `[APPROVED]` / `[REFUSED]` tokens are the only structured output the LLM is required to produce. Do not rename, internationalize, or add variants without updating:

- The system prompt instruction block that teaches the model the format.
- The `ChatPatch` parser that extracts the tag.
- `plan.md` Section 3.3.
- `TODO.md` adversarial test entries.
- Any debug hook that watches for the tag.

If a future variant becomes necessary (e.g. `[ESCALATE]`), introduce it as an additive token with a default fallback to `[CONTINUE]` so existing system prompts keep working.
