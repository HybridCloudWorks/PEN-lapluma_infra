#!/usr/bin/env python3
"""
Unit and invariant tests for operator grayscale styling, surface inventory,
and established frontends compliance (INF-12, INF-13, INT-10).
"""

from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from verify_design_spec import (
    contrast_ratio,
    relative_luminance,
    verify_operator_grayscale_compliance,
    verify_operator_surface_inventory,
    verify_shared_design_spec,
)


class TestOperatorGrayscale(unittest.TestCase):
    """Verifies grayscale rules, surface inventory, and established frontends policy."""

    def test_shared_design_spec_valid(self):
        errors = verify_shared_design_spec()
        self.assertEqual(errors, [], f"verify_shared_design_spec returned errors: {errors}")

    def test_operator_surface_inventory_valid(self):
        errors = verify_operator_surface_inventory()
        self.assertEqual(errors, [], f"verify_operator_surface_inventory returned errors: {errors}")

    def test_operator_grayscale_compliance_valid(self):
        errors = verify_operator_grayscale_compliance()
        self.assertEqual(errors, [], f"verify_operator_grayscale_compliance returned errors: {errors}")

    def test_wcag_contrast_ratios(self):
        self.assertGreaterEqual(contrast_ratio("#B3261E", "#FCE4E4"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#B3261E", "#FFFFFF"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#7D5700", "#FFF4CC"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#7D5700", "#FFFFFF"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#1B6E32", "#E3F3E8"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#1B6E32", "#FFFFFF"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#185ABC", "#E3EEFC"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#185ABC", "#FFFFFF"), 4.5)
        self.assertGreaterEqual(contrast_ratio("#202124", "#FFFFFF"), 7.0)

    def test_no_unapproved_frontend_frameworks(self):
        repo_root = TOOLS_DIR.parent
        disallowed = {".jsx", ".tsx", ".vue", ".svelte"}
        for search_dir in ["src", "tools", "infra"]:
            d = repo_root / search_dir
            if not d.is_dir():
                continue
            for p in d.rglob("*"):
                if p.suffix.lower() in disallowed:
                    self.fail(f"Found unapproved frontend file: {p.relative_to(repo_root)}")


if __name__ == "__main__":
    unittest.main()
