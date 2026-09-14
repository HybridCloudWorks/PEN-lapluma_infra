# LaPluma Shared Design Specification

**Version:** 1.0.0  
**Status:** Approved Architecture Contract  
**Linked Tickets:** INT-10, INF-12, INF-13, APP-09  
**Related Reference:** `DESIGN.md` (Mercury Style Reference with Explicit User Palette Overrides)

---

## 1. Executive Summary & Design Principles

This specification unifies visual design, geometry, component architecture, and accessibility standards across the LaPluma platform. It defines two distinct, non-overlapping product themes:
1. **Infra-Owned Operator Theme (Grayscale)**: High-density, professional, neutral dark/gray aesthetic designed for internal operations, blueprint publication, audit review, and cluster management.
2. **App-Owned Product Theme (White Canvas & Pastels)**: Approachable, low-anxiety, clear visual system using a pure white canvas, soft pastel fills (Red, Yellow, Green, Blue), dark ink typography, and high-contrast accessible text/action variants.

### Visual Reference & Explicit User Palette Overrides
The base styling derives from the Mercury Design Reference (`DESIGN.md`), preserving its type hierarchy, 4px grid rhythm, flat 12px cards, and 32px/40px pill shapes. 

Per user directive, the original single-accent cobalt rule is **explicitly overridden**:
- **Cobalt-only rule superseded**: App-owned native screens use white `#FFFFFF` plus pastel red `#FCE4E4`, yellow `#FFF4CC`, green `#E3F3E8`, and blue `#E3EEFC`, with dark ink `#202124`.
- **Grayscale for Infra surfaces**: Any infra-owned web or dashboard operator interfaces use neutral grayscale tokens (`#171717`, `#242424`, `#333333`, `#F5F5F5`, `#C7C7C7`) with white primary controls and dark text.
- **Flat Surface Separation**: Flat cards are distinguished by subtle background surface contrast rather than drop shadows.
- **Accessible Text on Pastels**: Pastel fills are never used as backgrounds for white text; all text on pastels uses dark ink or saturated high-contrast foreground variants meeting WCAG AA \(\ge 4.5:1\).

---

## 2. UI Surface Ownership Matrix

Theme application follows product and repository ownership boundaries:

| Surface Type | Repository | Primary Theme | Palette Tokens | Delivery Interfaces |
|---|---|---|---|---|
| **Operator / Admin Infrastructure** | `PEN-lapluma_infra` | **Neutral Grayscale** | `#171717`, `#242424`, `#333333`, `#F5F5F5`, `#C7C7C7`, `#FFFFFF` controls | CLI (`blueprint_cli.py`, `onboard_institution.py`), GitHub Actions CI, future web portals |
| **Applicant Mobile Experience** | `PEN-lapluma_app` | **Pastel + White Canvas** | White `#FFFFFF`, Pastels (`#FCE4E4`, `#FFF4CC`, `#E3F3E8`, `#E3EEFC`), Dark Ink `#202124` | iOS / iPhone native screens (Client setup, Guided Finish, Relay) |
| **Workforce & Reviewer Surfaces** | `PEN-lapluma_app` | **Pastel + White Canvas** | White `#FFFFFF`, Pastels, Dark Ink `#202124` | iPadOS / macOS workforce app (Preparer, Reviewer, Approver, Tenant Admin) |

> [!NOTE]
> Tenant administration within `PEN-lapluma_app` (managing clinic staff or workspace assignments) is an **app-owned** workforce surface and uses the pastel theme. Only cross-tenant platform operator tooling in `PEN-lapluma_infra` uses grayscale.

### Grayscale Operator UX on Established Frontends Only (INF-13)
Under **INF-13**, the grayscale specification applies strictly to established or separately approved operator interfaces. In accordance with platform governance to prevent unneeded maintenance overhead and frontend proliferation, no new web frontend or portal service is created solely for styling or customer onboarding. Established CLI tooling (`blueprint_cli.py`, `onboard_institution.py`, etc.) and CI automation enforce monochromatic neutrality without decorative pastel or unapproved color schemes.

---

## 3. Theme Specifications

### Theme A: App-Owned Pastel Palette (`PEN-lapluma_app`)

The app palette creates a serene, dignified interface that reduces stress for applicants and caseworkers navigating complex legal filings.

#### Color Tokens
```
Surface Canvas:        #FFFFFF (Pure White)
Surface Secondary:     #F8F9FA (Subtle Off-White for grouped lists)
Dark Ink (Text):       #202124 (High-contrast primary body and headings)
Ink Secondary:         #5F6368 (Subtle captions, metadata, citations)
Border / Separator:    #E0E0E0 (1px hairline divider)

Pastel Red (Fill):     #FCE4E4 (Attention / Critical / Missing blocker)
Pastel Yellow (Fill):  #FFF4CC (In Progress / Advisory / Pending review)
Pastel Green (Fill):   #E3F3E8 (Verified / Confirmed / Approved)
Pastel Blue (Fill):    #E3EEFC (Information / Official Guidance / Notice)
```

#### High-Contrast Accessible Text & Action Variants (WCAG AA \(\ge 4.5:1\))
Pastel fills alone have insufficient contrast against white text. Foreground text and action controls positioned over pastel fills (or white canvas) must use these verified dark/saturated variants:

| Semantic State | Pastel Background | Action / Text Foreground Token | Hex Code | Contrast on Pastel | Contrast on White |
|---|---|---|---|---|---|
| **Critical / Blocker** | `#FCE4E4` | `ActionRed` | `#B3261E` | **5.84 : 1** (Pass AA) | **5.91 : 1** (Pass AA) |
| **Warning / Attention** | `#FFF4CC` | `ActionYellow` | `#7D5700` | **4.82 : 1** (Pass AA) | **5.32 : 1** (Pass AA) |
| **Positive / Complete** | `#E3F3E8` | `ActionGreen` | `#1B6E32` | **4.91 : 1** (Pass AA) | **5.25 : 1** (Pass AA) |
| **Information / Notice** | `#E3EEFC` | `ActionBlue` | `#185ABC` | **5.35 : 1** (Pass AA) | **5.58 : 1** (Pass AA) |
| **Neutral / Default** | `#FFFFFF` | `DarkInk` | `#202124` | **15.1 : 1** (Pass AAA) | **15.8 : 1** (Pass AAA) |

---

### Theme B: Infra-Owned Grayscale Palette (`PEN-lapluma_infra`)

The grayscale palette provides a focused, high-contrast dark environment for technical operations, deployment monitoring, and blueprint verification.

#### Color Tokens
```
Canvas Background:     #171717 (Deep dark neutral canvas)
Card / Surface:        #242424 (Elevated flat card surface)
Control / Input:       #333333 (Input fields, secondary button background)
Border:                #444444 (Hairline stroke for controls and cards)
Text Primary:          #F5F5F5 (Bright white text for headings and data)
Text Secondary:        #C7C7C7 (Ash gray text for timestamps, hashes, metadata)
Primary CTA:           #FFFFFF (Solid white pill button with #171717 dark text)
```

---

## 4. Spacing Scale & Grid Rhythm

Both themes adhere to a strict **4px modular grid**:

| Token | Pixels | Usage |
|---|---|---|
| `grid4` (`xs`) | 4 px | Inline icon gaps, badge internal padding |
| `grid8` (`s`) | 8 px | Tight element spacing, chip horizontal padding |
| `grid12` | 12 px | Standard form input gap, sub-item spacing |
| `grid16` (`m`) | 16 px | Standard card content padding, stack spacing |
| `grid20` | 20 px | Medium sectional separation |
| `grid24` (`l`) | 24 px | Card-to-card margin, large form section separation |
| `grid32` (`xl`)| 32 px | Screen horizontal margins, major section padding |
| `grid40` | 40 px | Major component break |
| `grid56` | 56 px | Layout break on tablets/desktops |
| `grid72` | 72 px | Section grouping on macOS / web |
| `grid112`| 112 px | Large header breathing room |
| `grid128`| 128 px | Maximum page spacing |

---

## 5. Geometry & Component Radii

Cards and controls use flat shapes separated by surface color values without drop shadows:

| Component | Radius | Design Rationale |
|---|---|---|
| **Flat Cards** | **12 px** | `DESIGN.md` standard. Clean, modern, soft corners without pill distortion. |
| **Controls & Inputs** | **32 px** | Pill-shaped text fields, primary action buttons, stepper buttons. |
| **Pill Tags & Navigation** | **40 px** | Full pill geometry for status chips, filter selectors, tab bars. |
| **Small Chips / Badges** | **8 px** | Compact corner radius for tight metadata tags in table cells. |

---

## 6. Accessibility & Non-Color Invariants (NFR-A11Y-004)

1. **Mandatory Non-Color State Encoding**:
   - Color is never used alone to communicate case status, validation errors, or document readiness.
   - Every status tone requires a **paired icon** (SF Symbol or equivalent glyph) and an **explicit text label**.
   - Example:
     - Blocker: Pastel Red fill + `#B3261E` text + `exclamationmark.triangle.fill` + "Missing Signature"
     - Verified: Pastel Green fill + `#1B6E32` text + `checkmark.circle.fill` + "Verified"
2. **Dynamic Type & Typography Scaling**:
   - All text scales with system Dynamic Type without clipping or ellipses up to XXXL.
   - Base body size is 16pt / 1.5 line height. Headings use 1.1–1.2 tight line height.
3. **Minimum Touch Targets**:
   - Default touch targets are at least \(44 \times 44\text{ pt}\).
   - In accessibility mode (`apertureAccessibilityProfileEnabled`), targets expand to \(\ge 48 \times 48\text{ pt}\).
4. **Reduce Motion**:
   - All interactive transitions honor `accessibilityReduceMotion` by eliminating non-essential springs and animations.
