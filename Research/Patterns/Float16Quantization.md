---
title: Float16 quantization on network serialization
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:636-638 (F0066)
related: []
tags: [network]
---

# Float16 quantization on network serialization

Stationeers network serialization quantizes floats to half-precision. Values that are not exactly representable in `float16` round to the nearest representable value, which often differs from the user's input in visible ways. `0.2` becomes `0.2002...`, which prints as `0.202` in the UI.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0066 (Mods/PowerTransmitterPlus/RESEARCH.md:636-638):

> Network serialization quantizes floats to half-precision. `0.2` is not exactly representable; the nearest representable above is `0.2002...`, which prints as `0.202`. Useful for diagnosing "weird value just above the expected" reports.

Users reporting "I set the value to 0.2 but the UI shows 0.202" after a network round-trip are seeing float16 quantization, not a bug. The value stored server-side (before serialization) is 0.2 exactly; the value a client sees (after serialization) is the nearest representable half-precision float.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two approaches.

### Diagnostic: recognize the rounding

When a user reports a value slightly above or below the expected one, check whether the expected value is exactly representable in float16. Common near-miss anchors:

| Exact expected | Float16-rounded | UI display |
|---|---|---|
| 0.1 | 0.0999... | 0.0999 or 0.1 depending on formatter |
| 0.2 | 0.2002... | 0.202 |
| 0.3 | 0.2998... | 0.300 |
| 0.5 | 0.5 | 0.5 (exactly representable) |

Powers of two and their halves/quarters are exactly representable; most decimal fractions are not.

### Design: choose exactly-representable defaults

When setting defaults or step sizes for a mod-owned setting that will be network-synced, prefer values that are exactly representable in float16: 0.5, 0.25, 0.125, multiples of 1.0. Users who tune the setting in small increments will see their values round-trip unchanged.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0066).

## Open questions

None at creation.
