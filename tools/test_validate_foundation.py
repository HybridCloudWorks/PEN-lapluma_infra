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
    # Both halves of a connection string, assembled at runtime so neither this line nor any other
    # in this file carries both names contiguously — the rule matches a single line carrying both,
    # and a comment naming them together is enough to trip it.
    # The key value is short so this plants the connection-string rule and not the account-key one.
    "Azure storage connection string": "AccountName=lapluma;" + "Account" + "Key=short",
    "Azure storage account key": "Account" + "Key=" + "A" * 88,
    "shared access signature": "?" + "sig=" + "a" * 40,
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

    def test_a_connection_string_is_detected_whichever_order_its_parts_appear(self) -> None:
        # Connection strings are unordered key/value pairs. The previous rule matched one fixed
        # ordering, so the same credential written any other way passed the scan.
        for ordering in (
            "AccountName=lapluma;" + "Account" + "Key=short",
            "Account" + "Key=short;" + "AccountName=lapluma",
            "DefaultEndpointsProtocol=https;" + "Account" + "Key=short;AccountName=lapluma",
        ):
            with self.subTest(ordering=ordering):
                self.write("infra/candidate.bicep", ordering)
                findings = validate_foundation.scan_for_secrets(self.root)
                self.assertEqual(len(findings), 1, findings)
                self.assertTrue(findings[0].startswith("Azure storage connection string"), findings)

    def test_the_two_halves_of_a_connection_string_on_separate_lines_do_not_match(self) -> None:
        # The rule is line-scoped on purpose. Without that, this test file — which mentions both
        # names in nearby lines — would report itself.
        self.write("infra/candidate.bicep", "AccountName=lapluma\n" + "Account" + "Key=short\n")

        self.assertEqual(validate_foundation.scan_for_secrets(self.root), [])

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


class ParameterCollectionTests(unittest.TestCase):
    """The rule that keeps person, case, and eligibility identifiers out of the catalog API.

    It is only as good as what it can see, so these test what it collects rather than only that it
    passes against the current contract.
    """

    def test_operation_level_parameters_are_collected(self) -> None:
        names, failures = validate_foundation.collect_parameter_names(
            {"/v1/catalog/packages": {"get": {"parameters": [{"name": "categoryCode", "in": "query"}]}}}
        )

        self.assertEqual(names, {"categorycode"})
        self.assertEqual(failures, [])

    def test_path_item_level_parameters_are_collected(self) -> None:
        # OpenAPI permits `parameters` as a sibling of `get`, applying to every operation beneath
        # it. Reading operations alone missed these, so a prohibited input declared this way
        # walked straight past the gate.
        names, failures = validate_foundation.collect_parameter_names(
            {
                "/v1/catalog/packages": {
                    "parameters": [{"name": "caseId", "in": "query"}],
                    "get": {"parameters": [{"name": "categoryCode", "in": "query"}]},
                }
            }
        )

        self.assertEqual(names, {"caseid", "categorycode"})
        self.assertEqual(failures, [])

    def test_a_ref_parameter_is_rejected_rather_than_crashing(self) -> None:
        # A $ref declaration has no `name`; the previous code indexed it and died with a KeyError,
        # so CI failed with a traceback instead of a diagnosis.
        names, failures = validate_foundation.collect_parameter_names(
            {"/v1/catalog/packages": {"get": {"parameters": [{"$ref": "#/components/parameters/CaseId"}]}}}
        )

        self.assertEqual(names, set())
        self.assertEqual(len(failures), 1)
        self.assertIn("declared inline", failures[0])

    def test_a_malformed_declaration_is_reported_rather_than_crashing(self) -> None:
        # A rule whose job is to report a contract violation must not itself die reading one. A
        # non-object entry previously raised AttributeError or TypeError out of the collector, so
        # CI ended on a traceback rather than the ERROR line its log is scanned for.
        for entry in ("not-an-object", 42, None, ["nested"]):
            with self.subTest(entry=entry):
                names, failures = validate_foundation.collect_parameter_names(
                    {"/v1/catalog/packages": {"get": {"parameters": [entry]}}}
                )

                self.assertEqual(names, set())
                self.assertEqual(len(failures), 1)
                self.assertIn("not an object", failures[0])

    def test_a_parameters_value_that_is_not_a_list_is_reported(self) -> None:
        # Same reasoning one level up: `parameters` set to an object would have been iterated as
        # its keys, silently collecting nothing while looking like a clean pass.
        names, failures = validate_foundation.collect_parameter_names(
            {"/v1/catalog/packages": {"parameters": {"name": "caseId"}}}
        )

        self.assertEqual(names, set())
        self.assertEqual(len(failures), 1)
        self.assertIn("declared as a list", failures[0])

    def test_a_malformed_path_item_is_reported(self) -> None:
        names, failures = validate_foundation.collect_parameter_names(
            {"/v1/catalog/packages": "not-an-object"}
        )

        self.assertEqual(names, set())
        self.assertEqual(len(failures), 1)
        self.assertIn("path item is not an object", failures[0])

    def test_a_parameter_without_a_name_is_reported(self) -> None:
        _, failures = validate_foundation.collect_parameter_names(
            {"/v1/catalog/packages": {"get": {"parameters": [{"in": "query"}]}}}
        )

        self.assertEqual(len(failures), 1)
        self.assertIn("declares no name", failures[0])

    def test_every_prohibited_input_would_be_caught_at_path_item_level(self) -> None:
        for prohibited in validate_foundation.PROHIBITED_INPUTS:
            with self.subTest(prohibited=prohibited):
                names, _ = validate_foundation.collect_parameter_names(
                    {"/v1/catalog/packages": {"parameters": [{"name": prohibited, "in": "query"}]}}
                )
                self.assertIn(prohibited, names)


class ChecksAreIndependentTests(unittest.TestCase):
    def test_running_a_check_twice_does_not_accumulate_failures(self) -> None:
        # Each check owns its failures now. With a module-level global, a second run inherited the
        # first run's results, which is why these functions had no unit tests.
        first = validate_foundation.validate_openapi()
        second = validate_foundation.validate_openapi()

        self.assertEqual(list(first), list(second))
        self.assertEqual(list(first), [])


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

    def test_a_real_hostname_in_the_workflow_contract_is_rejected(self) -> None:
        tree = self.copy_tree()
        contract = tree / "contracts/openapi/documents-upload.yaml"
        self.rewrite(
            contract,
            'servers: [{url: "https://api.example.invalid/v1"}]',
            'servers: [{url: "https://api.lapluma.example.com/v1"}]',
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("placeholder server URL", result.stderr)

    def test_editing_the_workflow_mirror_without_adopting_a_revision_is_rejected(self) -> None:
        # The mirror must stay byte-identical to the adopted app revision. Any edit — even a
        # harmless-looking one — has to arrive as a deliberate adoption that updates the pin.
        tree = self.copy_tree()
        contract = tree / "contracts/openapi/workforce-workflow.yaml"
        self.rewrite(contract, "title: LaPluma Workflow API", "title: LaPluma Workflow API v2")
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("adopted contract revision", result.stderr)

    def test_dropping_the_idempotency_header_from_the_upload_contract_is_rejected(self) -> None:
        tree = self.copy_tree()
        contract = tree / "contracts/openapi/documents-upload.yaml"
        text = contract.read_text(encoding="utf-8").replace("Idempotency-Key", "X-Request-Key")
        contract.write_text(text, encoding="utf-8")
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("Idempotency-Key", result.stderr)

    def test_a_percentage_key_in_an_authored_contract_is_rejected(self) -> None:
        # The invariant gate the app's DevSecOps model marks "cannot be overridden". A percentage
        # reads to an applicant as a prediction that the case will be approved, which is the
        # eligibility judgement this product must never appear to make.
        tree = self.copy_tree()
        contract = tree / "contracts/openapi/documents-upload.yaml"
        self.rewrite(
            contract,
            "sizeBytes: {type: integer, minimum: 1, maximum: 104857600}",
            "sizeBytes: {type: integer, minimum: 1, maximum: 104857600}\n        percentComplete: {type: integer}",
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("percentage-like key: percentComplete", result.stderr)

    def test_a_completion_score_on_a_wire_model_is_rejected(self) -> None:
        tree = self.copy_tree()
        models = tree / "src/workflow-api/WorkflowModels.cs"
        self.rewrite(models, "    int AdvisoryItems);", "    int AdvisoryItems,\n    int CompletionScore);")
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("percentage-like member: CompletionScore", result.stderr)

    def test_the_percentage_rule_reads_through_separator_style(self) -> None:
        # pct_complete and pctComplete are the same field to a reader and to this rule.
        tree = self.copy_tree()
        models = tree / "src/workflow-api/WorkflowModels.cs"
        self.rewrite(models, "    int AdvisoryItems);", "    int AdvisoryItems,\n    int pct_complete);")
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("percentage-like member: pct_complete", result.stderr)

    def test_the_percentage_rule_does_not_trip_on_its_own_explanation(self) -> None:
        # The models and this repository discuss the ban in prose. A rule its own rationale trips
        # is a rule someone deletes, so comments are stripped before the code is scanned.
        tree = self.copy_tree()
        models = tree / "src/workflow-api/WorkflowModels.cs"
        models.write_text(
            "// No percentage or completion score may appear here: pctComplete is banned.\n"
            + models.read_text(encoding="utf-8"),
            encoding="utf-8",
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 0, result.stderr)

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

    def test_a_form_declared_twice_is_rejected(self) -> None:
        # A dict keyed by form number keeps only the last declaration, so a second one would
        # otherwise hide whatever the first said.
        tree = self.copy_tree()
        fixture = tree / "src/core-api/CatalogRepository.cs"
        self.rewrite(
            fixture,
            'Form("DS-11", "Application for a U.S. Passport", FormArtifactKind.OfficialPdf,\n'
            "                FormFillCapability.AutomaticFill, FormActivationState.CatalogOnly)",
            'Form("DS-11", "Application for a U.S. Passport", FormArtifactKind.OfficialPdf,\n'
            "                FormFillCapability.AutomaticFill, FormActivationState.CatalogOnly),\n"
            '            Form("DS-11", "Duplicate", FormArtifactKind.ExternalWorkflow,\n'
            "                FormFillCapability.ReferenceOnly, FormActivationState.CatalogOnly)",
        )
        result = self.run_validator(tree)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("declares a form more than once", result.stderr)

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
