---
title: TTS Dead End
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:618-646
  - Plans/LLM/RESEARCH.md:648-656
related:
  - ../GameSystems/Chat.md
tags: [dead-end, llm]
---

# TTS Dead End

Preserved investigation of every path for adding text-to-speech to a Stationeers mod. Each viable path has a disqualifying problem; combined, they make TTS too fragile to ship as a dependable feature. Reach for this page when a future contributor is about to re-investigate "should we add TTS to the chat bot," so they do not repeat the same three-day walk.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A contributor is weighing adding spoken output to chat messages, log events, or any narrative text surface.
- A new TTS engine or API surfaces and someone wonders whether it clears the blockers recorded here.

Use this page as the starting lookup. If a new engine plausibly clears every blocker listed below, add a dated note to Verification History and Open Questions; do not silently overwrite the existing findings.

## Per-path disqualifying problems
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Every viable path has a disqualifying problem. Combined, they make TTS too fragile to ship as a dependable feature.

**`System.Speech.Synthesis` does not work under Unity Mono.** Unity runs on Mono, not the full .NET Framework CLR. `System.Speech` is implemented via COM interop (`CoCreateInstance` for `SpVoice`). Mono's COM interop does not support this. The failure mode is a `TypeLoadException` at assembly load, before any code runs. This is documented across multiple BepInEx and Unity projects (MissionPlanner, ARKStatsExtractor). Even referencing the type from a code path that never executes causes the mod to fail to load.

**SAPI voices available through `GetInstalledVoices()` are limited.** On a clean Windows 10 or 11 install, SAPI exposes only Microsoft David (en-US male) and Microsoft Zira (en-US female). Windows 11 ships higher-quality "natural voices" (Aria, Jenny, Guy) but these are locked to the Narrator app, registered under `Speech_OneCore` registry key, invisible to SAPI. Installing language packs adds OneCore voices, not SAPI voices. Multilingual support through SAPI is not practical.

**SAPI does not handle language mismatch gracefully.** Given French text and an English voice, SAPI applies English phoneme rules to French orthography and spells out unknown words letter-by-letter. The output is garbled nonsense, not accented speech. This forces either a system prompt that constrains the LLM to English, or a language detection step with fallback logic for every response.

**Windows N/KN editions (EU/Korea) are uncertain.** SAPI is not explicitly listed among removed Media Feature Pack components, but Cortana speech features are documented as not working on N editions. Testing would be required on every edition variant to be sure.

**The native C++ DLL bridge workaround adds maintenance burden.** The proven pattern (Weisshaar blog, UnityWindowsTTS, UnityAccessibilityLib) is to write a small C++ DLL that calls SAPI via native COM, expose C functions via P/Invoke. Works reliably. But it means:

- A second build toolchain (MSVC + C++ project) alongside the .NET Framework mod
- Shipping an x64 native DLL per platform
- Debugging across the managed/native boundary when something breaks
- No Unity audio pipeline integration (audio goes directly to OS device)

**Neural TTS engines all have blockers.**

- **Piper**: high-quality voices, 100+ languages, but uses espeak-ng for phonemization. espeak-ng is GPL 3.0, which is incompatible with the mod's Apache 2.0 license. Distributing espeak-ng would force the mod to GPL. The license is one-way compatible (Apache into GPL, not the reverse).
- **Kokoro via KokoroSharp**: state-of-the-art voices, pure C# NuGet, but depends on ONNX Runtime. ONNX Runtime >= 1.15.0 has documented crash issues with Unity Mono (`System.Buffer.InternalMemcpy` failures, issue #18441 on microsoft/onnxruntime). Adds ~320 MB model and a new native dependency stack that conflicts with how LLamaSharp is currently wired up. Also uses espeak-ng for phonemization (same GPL issue).
- **Sherpa-ONNX**: comprehensive but heavyweight, requires Unity 2022.3+, same ONNX/Mono risk.

**Cloud TTS APIs (OpenAI, Azure, ElevenLabs)** would add:

- Network dependency (defeats "no downloads at runtime")
- API key management per player
- Per-token costs
- Privacy implications (sending chat text to a third-party)
- Latency (network round-trip per response)

## Combined risk assessment
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Every viable path requires one of:

- A native C++ DLL we build and maintain ourselves
- A GPL license that changes the mod's licensing
- An ONNX Runtime stack with known Mono crashes
- A cloud service with API keys and network dependency
- Acceptance that non-English speakers get garbled output

None of these match the mod's design goals: zero runtime downloads, Apache 2.0 licensing, universal compatibility, self-contained distribution.

## Current decision
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Ship text-only. If a contributor later wants to add TTS as an optional client-side add-on, the path of least resistance is the native C++ DLL bridge to SAPI (radio filter: low-pass at 3kHz + reverb through Unity audio filters). That would be a separate mod, not part of the LLM mod itself.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0094 and F0095 (`Plans/LLM/RESEARCH.md:618-656`). Mirror decision recorded from F0095u.

## Open questions

None at creation. If a new TTS engine surfaces that plausibly clears every blocker, add it here as a candidate with a dated note rather than overwriting the above.
