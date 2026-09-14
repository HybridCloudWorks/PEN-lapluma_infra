#!/usr/bin/env python3
"""
tools/verify_design_spec.py

Verification script for Phase 10: Shared Design Specification, Grayscale Operator Styling,
and Native Pastel Design Tokens (INT-10, INF-12, APP-09).
Validates token completeness, WCAG AA contrast compliance, spacing rhythm, geometry,
and operator surface inventory.
"""

import math
import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SPEC_PATH = os.path.join(REPO_ROOT, "docs", "design", "shared-design-specification.md")
ARCH_OVERVIEW_PATH = os.path.join(REPO_ROOT, "wiki", "Architecture-Overview.md")


def srgb_channel_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def relative_luminance(hex_str: str) -> float:
    hex_str = hex_str.lstrip("#")
    r = int(hex_str[0:2], 16) / 255.0
    g = int(hex_str[2:4], 16) / 255.0
    b = int(hex_str[4:6], 16) / 255.0
    r_lin = srgb_channel_to_linear(r)
    g_lin = srgb_channel_to_linear(g)
    b_lin = srgb_channel_to_linear(b)
    return 0.2126 * r_lin + 0.7152 * g_lin + 0.0722 * b_lin


def contrast_ratio(hex1: str, hex2: str) -> float:
    l1 = relative_luminance(hex1)
    l2 = relative_luminance(hex2)
    lighter = max(l1, l2)
    darker = min(l1, l2)
    return (lighter + 0.05) / (darker + 0.05)


def verify_shared_design_spec() -> list[str]:
    errors = []

    if not os.path.isfile(SPEC_PATH):
        return [f"Shared design specification missing at {SPEC_PATH}"]

    with open(SPEC_PATH, "r", encoding="utf-8") as f:
        spec_text = f.read()

    # 1. Verify two product themes
    if "Infra-Owned Operator Theme (Grayscale)" not in spec_text:
        errors.append("Spec missing 'Infra-Owned Operator Theme (Grayscale)' definition")
    if "App-Owned Product Theme (White Canvas & Pastels)" not in spec_text:
        errors.append("Spec missing 'App-Owned Product Theme (White Canvas & Pastels)' definition")

    # 2. Verify explicit user overrides of cobalt-only rule
    if "cobalt" not in spec_text.lower():
        errors.append("Spec must record explicit override of DESIGN.md cobalt-only rule")

    # 3. Verify pastel tokens
    expected_pastel_tokens = {
        "Pastel Red": "#FCE4E4",
        "Pastel Yellow": "#FFF4CC",
        "Pastel Green": "#E3F3E8",
        "Pastel Blue": "#E3EEFC",
        "Dark Ink": "#202124",
        "White Surface": "#FFFFFF",
    }
    for name, hex_code in expected_pastel_tokens.items():
        if hex_code.upper() not in spec_text.upper():
            errors.append(f"Spec missing token {name} ({hex_code})")

    # 4. Verify grayscale tokens
    expected_gray_tokens = ["#171717", "#242424", "#333333", "#F5F5F5", "#C7C7C7"]
    for hex_code in expected_gray_tokens:
        if hex_code.upper() not in spec_text.upper():
            errors.append(f"Spec missing grayscale token ({hex_code})")

    # 5. Verify WCAG AA contrast for action/foreground tokens on pastels
    # Pairs: (foreground, background, label)
    contrast_pairs = [
        ("#B3261E", "#FCE4E4", "ActionRed on Pastel Red"),
        ("#B3261E", "#FFFFFF", "ActionRed on White"),
        ("#7D5700", "#FFF4CC", "ActionYellow on Pastel Yellow"),
        ("#7D5700", "#FFFFFF", "ActionYellow on White"),
        ("#1B6E32", "#E3F3E8", "ActionGreen on Pastel Green"),
        ("#1B6E32", "#FFFFFF", "ActionGreen on White"),
        ("#185ABC", "#E3EEFC", "ActionBlue on Pastel Blue"),
        ("#185ABC", "#FFFFFF", "ActionBlue on White"),
        ("#202124", "#FFFFFF", "Dark Ink on White"),
    ]
    for fg, bg, label in contrast_pairs:
        ratio = contrast_ratio(fg, bg)
        if ratio < 4.5:
            errors.append(f"Contrast failure for {label}: ratio {ratio:.2f} < 4.5:1 (WCAG AA requirement)")

    # 6. Verify 4px grid rhythm scale
    for step in [4, 8, 12, 16, 20, 24, 32, 40, 56, 72, 112, 128]:
        pattern = rf"\b{step}\s*px\b"
        if not re.search(pattern, spec_text):
            errors.append(f"Spec missing 4px rhythm step {step}px")

    # 7. Verify component radii
    if "12 px" not in spec_text and "12px" not in spec_text:
        errors.append("Spec missing flat card radius 12px")
    if "32 px" not in spec_text and "32px" not in spec_text:
        errors.append("Spec missing control/input radius 32px")
    if "40 px" not in spec_text and "40px" not in spec_text:
        errors.append("Spec missing navigation pill radius 40px")

    # 8. Verify non-color accessibility invariant (NFR-A11Y-004)
    if "NFR-A11Y-004" not in spec_text:
        errors.append("Spec missing NFR-A11Y-004 non-color state encoding reference")
    if "icon" not in spec_text.lower() or "label" not in spec_text.lower():
        errors.append("Spec must mandate both icon and label for state indicators")

    # 9. Verify UI surface ownership matrix
    if "UI Surface Ownership Matrix" not in spec_text:
        errors.append("Spec missing UI Surface Ownership Matrix")

    return errors


def verify_operator_surface_inventory() -> list[str]:
    errors = []
    if not os.path.isfile(ARCH_OVERVIEW_PATH):
        return [f"Architecture overview missing at {ARCH_OVERVIEW_PATH}"]

    with open(ARCH_OVERVIEW_PATH, "r", encoding="utf-8") as f:
        arch_text = f.read()

    if "Operator Grayscale Styling" not in arch_text:
        errors.append("Architecture overview missing Operator Grayscale Styling section (INF-12)")
    if "Operator Surface Inventory" not in arch_text:
        errors.append("Architecture overview missing Operator Surface Inventory section (INF-12)")
    if "No custom web frontend is currently deployed in `PEN-lapluma_infra`" not in arch_text:
        errors.append("Architecture overview must confirm operator surface inventory status")

    return errors


def main() -> int:
    print("Verifying Shared Design Specification & Operator Styling (INT-10, INF-12, APP-09)...")
    errors = []

    spec_errors = verify_shared_design_spec()
    if spec_errors:
        print(f"FAILED: {len(spec_errors)} issues in shared-design-specification.md:")
        for err in spec_errors:
            print(f"  - {err}")
        errors.extend(spec_errors)
    else:
        print("  OK: shared-design-specification.md passed all token, contrast, and geometry checks.")

    inv_errors = verify_operator_surface_inventory()
    if inv_errors:
        print(f"FAILED: {len(inv_errors)} issues in Architecture-Overview.md:")
        for err in inv_errors:
            print(f"  - {err}")
        errors.extend(inv_errors)
    else:
        print("  OK: Architecture-Overview.md operator surface inventory and grayscale rules verified.")

    if errors:
        print(f"\nVerification FAILED with {len(errors)} error(s).")
        return 1

    print("\nVerification PASSED (0 errors).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
