# LLM Mod: Local Conventions

Supplements the monorepo root `CLAUDE.md`. Only rules specific to the LLM mod live here. Every rule in the root `CLAUDE.md` still applies.

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

- Developer-local copy for source builds: `LLM/models/<filename>` inside the mod folder (gitignored).
- End-user deploy: `mods/LLM/models/<filename>` next to `LLM.dll` in the Stationeers mods directory.
- Archive/backup: outside the repo entirely. Canonical source URLs documented in `README.md`.

Why:

- Model weights routinely run 500 MB to multi-GB. GitHub caps a single non-LFS file at 100 MB and the free LFS tier at 1 GB storage plus 1 GB/month bandwidth. A single GGUF blows through that quota on the first commit.
- Every `git clone` would otherwise download the weights. Bandwidth cost compounds per contributor and per CI run.
- Model files are commodity artifacts with stable download URLs. `README.md` is the source of truth for where to fetch them.

When adding a new quantization or a different model format:

- Confirm its extension is already caught by a `.gitignore` pattern. If not, add one at the repo root alongside the existing `*.gguf` entry.
- Document the expected filename, size, and download URL under the "Model file" section of `Plans/LLM/README.md`.
- Do not commit a "just for testing" copy. History retention makes that permanent even if reverted.

Exception: if a genuinely small synthetic model fixture is needed for automated tests (under 10 MB, clearly not a real model), it may live under `LLM/tests/fixtures/`. Anything larger needs the same discussion as a full model before committing.
