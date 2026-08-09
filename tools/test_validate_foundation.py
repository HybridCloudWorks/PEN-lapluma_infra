"""Tests for the secret scan's coverage and its exclusions.

Narrowing the walk to skip generated output must not narrow what the scan catches in source.
Every pattern is assembled at runtime from fragments so that this file never contains a scanned
pattern as a contiguous literal — otherwise the scan would report the test that guards it.
"""

from __future__ import annotations

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


if __name__ == "__main__":
    unittest.main()
