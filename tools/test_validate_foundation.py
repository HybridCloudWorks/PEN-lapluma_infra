"""Tests for the secret scan's coverage and its exclusions.

Narrowing the walk to skip generated output must not narrow what the scan catches in source.
Every pattern is assembled at runtime from fragments so that this file never contains a scanned
pattern as a contiguous literal — otherwise the scan would report the test that guards it.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import validate_foundation


PLANTED = {
    "private key": "-----BEGIN " + "PRIVATE KEY-----",
    "Azure storage connection string": "DefaultEndpointsProtocol=https;" + "AccountName=lapluma",
    "JWT-like token": "eyJ" + "a" * 24 + "." + "b" * 24 + "." + "c" * 16,
    "concrete subscription assignment": "AZURE_SUBSCRIPTION_ID" + "=" + "0" * 36,
    "concrete tenant assignment": "AZURE_TENANT_ID" + "=" + "0" * 36,
}


class ScanForSecretsTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def write(self, relative: str, text: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def test_every_pattern_is_detected_in_a_scanned_file(self) -> None:
        for label, planted in PLANTED.items():
            with self.subTest(pattern=label):
                self.write("infra/candidate.bicep", planted)
                findings = validate_foundation.scan_for_secrets(self.root)
                self.assertEqual(len(findings), 1, findings)
                self.assertTrue(findings[0].startswith(label), findings[0])

    def test_generated_directories_are_skipped(self) -> None:
        planted = PLANTED["Azure storage connection string"]
        for directory in ("__pycache__", "bin", "obj", ".venv", "node_modules", ".git"):
            self.write(f"tools/{directory}/generated.txt", planted)
        self.assertEqual(validate_foundation.scan_for_secrets(self.root), [])

    def test_a_directory_named_like_a_skipped_one_deeper_in_the_tree_is_still_skipped(self) -> None:
        self.write("src/core-api/obj/Debug/artifact.txt", PLANTED["private key"])
        self.assertEqual(validate_foundation.scan_for_secrets(self.root), [])

    def test_a_file_named_like_a_skipped_directory_is_still_scanned(self) -> None:
        self.write("infra/obj", PLANTED["private key"])
        self.assertEqual(len(validate_foundation.scan_for_secrets(self.root)), 1)

    def test_binary_suffixes_are_skipped(self) -> None:
        self.write("docs/diagram.png", PLANTED["JWT-like token"])
        self.assertEqual(validate_foundation.scan_for_secrets(self.root), [])

    def test_explicitly_ignored_files_are_skipped(self) -> None:
        path = self.write("tools/scanner.py", PLANTED["Azure storage connection string"])
        self.assertEqual(
            validate_foundation.scan_for_secrets(self.root, frozenset({path.resolve()})),
            [],
        )

    def test_the_repository_tree_itself_is_clean(self) -> None:
        findings = validate_foundation.scan_for_secrets(
            validate_foundation.ROOT,
            frozenset({Path(validate_foundation.__file__).resolve()}),
        )
        self.assertEqual(findings, [])


class CatalogInvariantTests(unittest.TestCase):
    """The catalog rules must reject semantic drift, not merely the absence of a literal.

    Each test mutates a throwaway copy of the tree and runs the validator against it as a
    subprocess, so the rules are exercised end to end exactly as CI runs them.
    """

    def copy_tree(self) -> Path:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        target = Path(temporary.name) / "repository"
        shutil.copytree(
            validate_foundation.ROOT,
            target,
            ignore=shutil.ignore_patterns(".git", "__pycache__", ".venv"),
        )
        return target

    def run_validator(self, tree: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, "tools/validate_foundation.py"],
            cwd=tree,
            capture_output=True,
            text=True,
        )

    def rewrite(self, path: Path, old: str, new: str) -> None:
        """Apply a mutation and prove it landed.

        A mutation that silently matches nothing leaves the tree pristine, and the assertion
        that follows then passes for the wrong reason.
        """
        original = path.read_text(encoding="utf-8")
        mutated = original.replace(old, new, 1)
        self.assertNotEqual(original, mutated, f"mutation did not apply to {path.name}")
        path.write_text(mutated, encoding="utf-8")

    def test_an_unmutated_copy_still_passes(self) -> None:
        # Proves the harness itself works, so a failure below means the mutation was caught
        # rather than that the copy was broken.
        result = self.run_validator(self.copy_tree())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_swapping_classifications_between_forms_is_rejected(self) -> None:
        tree = self.copy_tree()
        fixture = tree / "src/core-api/CatalogRepository.cs"
        self.rewrite(
            fixture,
            "FormArtifactKind.ExternalWorkflow, FormFillCapability.ReferenceOnly,",
            "FormArtifactKind.OfficialPdf, FormFillCapability.AutomaticFill,",
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("FAFSA must remain an external workflow", result.stderr)

    def test_a_retired_form_in_the_acquisition_scope_is_rejected(self) -> None:
        tree = self.copy_tree()
        contract = tree / "src/functions/acquisition_contract.py"
        self.rewrite(
            contract,
            '"DS-11", "FAFSA"]',
            '"DS-11", "FAFSA", "N-400"]',
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("N-400 leaked into the acquisition scope", result.stderr)

    def test_activating_a_pilot_edition_is_rejected(self) -> None:
        tree = self.copy_tree()
        fixture = tree / "src/core-api/CatalogRepository.cs"
        self.rewrite(fixture, "FormActivationState.Unavailable))", "FormActivationState.Pilot))")
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("pilot edition", result.stderr)

    def test_the_parser_binds_each_form_to_its_own_classification(self) -> None:
        source = (validate_foundation.ROOT / "src/core-api/CatalogRepository.cs").read_text(
            encoding="utf-8"
        )
        classifications = validate_foundation.parse_form_classifications(source)
        self.assertEqual(
            classifications["FAFSA"], ("ExternalWorkflow", "ReferenceOnly", "Unavailable")
        )
        self.assertEqual(classifications["I-130"], ("OfficialPdf", "AutomaticFill", "CatalogOnly"))

    def test_an_unparseable_fixture_is_not_silently_accepted(self) -> None:
        # An empty parse must read as "the fixture drifted", never as "no form breaks a rule".
        self.assertEqual(validate_foundation.parse_form_classifications(""), {})
        tree = self.copy_tree()
        fixture = tree / "src/core-api/CatalogRepository.cs"
        fixture.write_text(
            'class CatalogRepository { /* FAMILY_I130 "I-130A" ADJUSTMENT_I485_I864 "I-864" '
            'I-130 I-485 DS-11 FAFSA */ }',
            encoding="utf-8",
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("form set drifted", result.stderr)


if __name__ == "__main__":
    unittest.main()
