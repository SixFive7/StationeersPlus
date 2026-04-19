# {{Mod Display Name}} Research

Durable, project-scoped internals. Architecture, patch catalog with formulas, decompiled game internals, multiplayer protocol, pitfalls, and design decisions with rationale. No session state, no developer-specific paths, no references to automation.

## Architecture

{{High-level overview of how the mod is organised. Key classes, how they talk to each other, what lives where.}}

## File walkthrough

{{One short paragraph per `.cs` file explaining its responsibility. A new contributor should be able to locate any feature by reading this section.}}

## Patch catalog

{{Each Harmony patch by target method, what it changes, and why. Include formulas and constants inline.}}

## Decompiled game internals

{{Non-obvious findings from reading the vanilla game DLLs. IL patterns, UI event plumbing, multiplayer quirks, save-load ordering. Cite `game.cs` line numbers.}}

## Multiplayer protocol

{{Custom network messages, handshake version enforcement, serialise/deserialise on join, NetworkUpdateFlag allocations. Omit this section if the mod is single-player-only.}}

## Pitfalls

{{Things that look fine but bite. Previous bugs and their root cause.}}

## Design decisions

{{One short entry per decision: what was decided, what the alternatives were, why this one was picked.}}
