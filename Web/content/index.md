---
title: StationeersPlus
hide:
  - navigation
  - toc
---

# StationeersPlus

A monorepo of mods, interactive tools, and decompile-sourced research notes for [Stationeers](https://store.steampowered.com/app/544550/Stationeers/). This site is the public face of two things kept in that repo: a small set of interactive tools, and a structured knowledge base of how the game actually works under the hood.

<div class="grid cards" markdown>

-   :material-tools:{ .lg .middle } **Tools**

    ---

    Interactive single-page utilities. Browser-only, no install required. Sourced verbatim from the game's decompile, with the cited line numbers in view.

    [:octicons-arrow-right-24: Open the tools](tools/index.md)

-   :material-book-open-variant:{ .lg .middle } **Research**

    ---

    A structured knowledge base of Stationeers' internals: classes, systems, patterns, protocols, and workflows. Every page carries a verification stamp against a specific game version and cites the originating DLL or research file.

    [:octicons-arrow-right-24: Browse the knowledge base](research/index.md)

</div>

## How this site exists

Both halves come from the same place. The `Research/` directory in the [StationeersPlus repository](https://github.com/SixFive7/StationeersPlus) is curated as the working knowledge base for a small team of human developers and AI coding agents collaborating on a set of Stationeers mods. Findings are pulled from decompiled game code, validated against a specific game version, and stamped. The `tools/` directory holds interactive HTML utilities that pin a particular piece of game data into a navigable form.

This site mirrors both directories with no editorial layer between source and reader. When a research page is updated in the repo, it shows up here on the next publish. When a tool ships in `tools/`, it appears in the Tools section automatically.

Everything is under Apache 2.0. Mods, tools, research, this site.
