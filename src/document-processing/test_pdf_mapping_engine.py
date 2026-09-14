"""Unit and golden tests for the declarative PDF mapping engine (INF-05).

Tests official AcroForm mapping fidelity, Unicode handling, repeat/overflow
addendum generation, static assisted workflows, external reference handling,
and security sanitization against malicious scripts.
"""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

SRC_DIR = Path(__file__).resolve().parent
if str(SRC_DIR) not in sys.path:
    sys.path.insert(0, str(SRC_DIR))

from pdf_mapping_engine import (
    AddendumEntry,
    MappingResult,
    OverflowStrategy,
    PdfMappingEngine,
    PreparationMode,
)

ROOT = SRC_DIR.parents[1]
I130_PATH = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
CLINIC_PATH = ROOT / "blueprints/institutional/clinic-intake/blueprint.json"
FAFSA_PATH = ROOT / "blueprints/external/student-aid-fafsa/blueprint.json"


class TestPdfMappingEngine(unittest.TestCase):
    def setUp(self) -> None:
        self.engine = PdfMappingEngine()
        self.i130_bp = json.loads(I130_PATH.read_text(encoding="utf-8"))
        self.clinic_bp = json.loads(CLINIC_PATH.read_text(encoding="utf-8"))
        self.fafsa_bp = json.loads(FAFSA_PATH.read_text(encoding="utf-8"))

    def test_fillable_pdf_official_fidelity_mapping(self) -> None:
        """Test official AcroForm field mapping for I-130 petition."""
        inputs = {
            "petitioner.given_name": "Maria",
            "petitioner.family_name": "Santos",
            "petitioner.dob": "1985-06-15",
            "petitioner.ssn": "123-45-6789",
            "beneficiary.given_name": "Carlos",
            "beneficiary.family_name": "Santos",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2015-10-24",
        }
        res = self.engine.process(self.i130_bp, inputs)
        self.assertTrue(res.is_valid, f"Validation errors: {res.errors}")
        self.assertEqual(res.preparation_mode, PreparationMode.FILLABLE_PDF.value)
        self.assertEqual(
            res.pdf_field_values["form1[0].#subform[0].Pt1Line1a_GivenName[0]"],
            "Maria",
        )
        self.assertEqual(
            res.pdf_field_values["form1[0].#subform[0].Pt1Line1b_FamilyName[0]"],
            "Santos",
        )
        self.assertEqual(len(res.overflow_addendum), 0)

    def test_unicode_normalization_and_multi_script(self) -> None:
        """Test that Unicode multi-script names, accents, and diacritics are preserved in NFC form."""
        inputs = {
            "petitioner.given_name": "José María",
            "petitioner.family_name": "Müller-Lüdenscheidt",
            "petitioner.dob": "1990-01-01",
            "beneficiary.given_name": "李小龙",
            "beneficiary.family_name": "Владимир",
            "beneficiary.relationship": "Child",
        }
        res = self.engine.process(self.i130_bp, inputs)
        self.assertTrue(res.is_valid, f"Validation errors: {res.errors}")
        self.assertEqual(
            res.pdf_field_values["form1[0].#subform[0].Pt1Line1a_GivenName[0]"],
            "José María",
        )
        self.assertEqual(
            res.pdf_field_values["form1[0].#subform[0].Pt1Line1b_FamilyName[0]"],
            "Müller-Lüdenscheidt",
        )

    def test_repeat_section_inline_and_overflow_addendum(self) -> None:
        """Test repeated array fields: slot 0 maps inline, excess entries overflow to Addendum."""
        inputs = {
            "petitioner.given_name": "Jane",
            "petitioner.family_name": "Doe",
            "petitioner.dob": "1980-01-01",
            "beneficiary.given_name": "John",
            "beneficiary.family_name": "Doe",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2010-05-20",
            # Child 0 maps inline: Pt4Line0a_ChildName[0]
            "beneficiary_children[0].name": "Alice Doe",
            # Child 1 maps inline: Pt4Line1a_ChildName[0]
            "beneficiary_children[1].name": "Bob Doe",
            # Children 5+ exceeds section maxOccurs=5 and overflows to Addendum
            "beneficiary_children[5].name": "Charlie Doe (Overflow)",
        }
        res = self.engine.process(self.i130_bp, inputs)
        self.assertTrue(res.is_valid, f"Validation errors: {res.errors}")
        self.assertEqual(
            res.pdf_field_values.get("form1[0].#subform[0].Pt4Line0a_ChildName[0]"),
            "Alice Doe",
        )
        self.assertEqual(
            res.pdf_field_values.get("form1[0].#subform[0].Pt4Line1a_ChildName[0]"),
            "Bob Doe",
        )
        self.assertEqual(len(res.overflow_addendum), 1)
        addendum = res.overflow_addendum[0]
        self.assertEqual(addendum["canonicalPath"], "beneficiary_children[5].name")
        self.assertEqual(addendum["content"], "Charlie Doe (Overflow)")
        self.assertIn("Exceeded form capacity", addendum["explanation"])

    def test_character_length_overflow_generates_addendum(self) -> None:
        """Test that strings exceeding maxLength generate addendum entries when ATTACHMENT_ADDENDUM is set."""
        long_name = "Alexander Bartholomew Maximillian Christopher The Third"
        inputs = {
            "petitioner.given_name": long_name,  # maxLength is 30
            "petitioner.family_name": "Smith",
            "petitioner.dob": "1985-01-01",
            "beneficiary.given_name": "Sarah",
            "beneficiary.family_name": "Smith",
            "beneficiary.relationship": "Child",
        }
        res = self.engine.process(self.i130_bp, inputs)
        self.assertTrue(res.is_valid, f"Validation errors: {res.errors}")
        # Inline PDF field is truncated to maxLength (30 chars)
        field_val = res.pdf_field_values["form1[0].#subform[0].Pt1Line1a_GivenName[0]"]
        self.assertEqual(len(field_val), 30)
        self.assertEqual(field_val, long_name[:30])
        # Full content placed in addendum
        self.assertEqual(len(res.overflow_addendum), 1)
        self.assertEqual(res.overflow_addendum[0]["content"], long_name)
        self.assertIn("exceeded character limit", res.overflow_addendum[0]["explanation"])

    def test_static_assisted_checklist_generation(self) -> None:
        """Test STATIC_ASSISTED preparation for clinic intake document."""
        inputs = {
            "client.full_name": "Carlos Gomez",
            "client.household_size": 4,
            "client.annual_income": 28000,
            "client.eligible_for_fee_waiver": True,
        }
        res = self.engine.process(self.clinic_bp, inputs)
        self.assertTrue(res.is_valid, f"Validation errors: {res.errors}")
        self.assertEqual(res.preparation_mode, PreparationMode.STATIC_ASSISTED.value)
        self.assertEqual(len(res.assisted_checklist), 4)
        for item in res.assisted_checklist:
            self.assertEqual(item["status"], "COMPLETED")
        self.assertEqual(res.assisted_checklist[0]["value"], "Carlos Gomez")
        self.assertEqual(len(res.pdf_field_values), 0)

    def test_static_assisted_missing_required_item(self) -> None:
        """Test STATIC_ASSISTED fails when required intake field is missing."""
        inputs = {
            "client.full_name": "Carlos Gomez",
            "client.household_size": 4,
            # missing annual_income and eligible_for_fee_waiver
        }
        res = self.engine.process(self.clinic_bp, inputs)
        self.assertFalse(res.is_valid)
        self.assertIn("Missing required intake item: 'client.annual_income'", res.errors)

    def test_external_reference_guidance(self) -> None:
        """Test EXTERNAL_REFERENCE preparation for FAFSA portal workflow."""
        inputs = {
            "student.name": "Alex Smith",
            "student.academic_year": "2026-2027",
        }
        res = self.engine.process(self.fafsa_bp, inputs)
        self.assertTrue(res.is_valid)
        self.assertEqual(res.preparation_mode, PreparationMode.EXTERNAL_REFERENCE.value)
        self.assertIsNotNone(res.external_reference)
        self.assertEqual(
            res.external_reference["officialPortalUrl"],
            "https://studentaid.gov/h/apply-for-aid/fafsa",
        )
        self.assertEqual(len(res.external_reference["prerequisites"]), 1)
        self.assertEqual(
            res.external_reference["prerequisites"][0]["code"],
            "FAFSA_CONFIRMATION",
        )

    def test_malicious_script_injection_rejected(self) -> None:
        """Test that values with embedded JavaScript or executable constructs are rejected."""
        malicious_payloads = [
            "<script>alert('pwned')</script>",
            "/JavaScript << /JS (app.alert(1)) >>",
            "eval('import os; os.system(\"rm -rf /\")')",
            "javascript:void(0)",
            "__proto__.polluted = true",
        ]
        for payload in malicious_payloads:
            inputs = {
                "petitioner.given_name": payload,
                "petitioner.family_name": "Doe",
                "petitioner.dob": "1980-01-01",
                "beneficiary.given_name": "Jane",
                "beneficiary.family_name": "Doe",
                "beneficiary.relationship": "Child",
            }
            res = self.engine.process(self.i130_bp, inputs)
            self.assertFalse(
                res.is_valid,
                f"Payload {payload!r} should have been rejected by security checks",
            )
            self.assertTrue(
                any("Malicious script" in err for err in res.errors),
                f"Expected security error for payload {payload!r}, got: {res.errors}",
            )

    def test_golden_output_determinism(self) -> None:
        """Verify that mapping output produces byte-exact deterministic golden JSON."""
        inputs = {
            "petitioner.given_name": "María",
            "petitioner.family_name": "Santos",
            "petitioner.dob": "1985-06-15",
            "petitioner.ssn": "123-45-6789",
            "beneficiary.given_name": "Carlos",
            "beneficiary.family_name": "Santos",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2015-10-24",
        }
        res1 = self.engine.process(self.i130_bp, inputs)
        res2 = self.engine.process(self.i130_bp, inputs)

        # Discard dynamic timestamp in auditMetadata before comparing
        d1 = res1.to_dict()
        d2 = res2.to_dict()
        d1["auditMetadata"]["timestamp"] = "STATIC_TIMESTAMP"
        d2["auditMetadata"]["timestamp"] = "STATIC_TIMESTAMP"

        json1 = json.dumps(d1, sort_keys=True, indent=2)
        json2 = json.dumps(d2, sort_keys=True, indent=2)
        self.assertEqual(json1, json2)


if __name__ == "__main__":
    unittest.main()
