"""Generator for official source-cited document guidance (INF-09)."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = ROOT / "contracts" / "uscis-official-manifest.json"
GUIDANCE_PATH = ROOT / "contracts" / "uscis-official-guidance.json"

with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
    manifest = json.load(f)

# Specific statutory fee mappings in USD cents where uniform and fixed (ADR-019 / INF-09: zero guessing)
KNOWN_UNIFORM_FEES = {
    "I-130": 67500,       # $675 paper filing fee
    "I-130A": None,       # Supplement, no additional fee
    "I-90": 45500,        # $455 paper filing fee
    "I-485": 144000,      # $1,440 standard adjustment fee
    "I-864": None,        # No separate fee when filed with USCIS
    "I-864A": None,       # Contract supplement, no separate fee
    "I-864EZ": None,      # Short-form affidavit, no separate fee
    "I-864W": None,       # Exemption form, no fee
    "G-28": None,         # Entry of Appearance, no fee
    "G-1145": None,       # E-Notification request, no fee
    "G-1450": None,       # Credit card authorization, no fee
    "AR-11": None,        # Change of address, no fee
    "I-9": None,          # Employment verification, internal employer record, no fee
}

# Standard series-based evidence checklists
def make_evidence_checklist(form: dict) -> list[str]:
    series = form.get("series", "")
    fid = form["formId"]
    
    if fid == "I-130":
        return [
            "Proof of petitioner's U.S. citizenship or lawful permanent resident status (e.g., birth certificate, passport, naturalization certificate, or green card)",
            "Civil marriage certificate or legal proof of familial relationship",
            "Proof of legal termination of any prior marriages for both parties",
            "Two identical passport-style photographs of both petitioner and beneficiary"
        ]
    elif fid == "I-130A":
        return [
            "Employment history for the beneficiary for the previous five years",
            "Residential address history for the beneficiary for the previous five years"
        ]
    elif fid == "I-485":
        return [
            "Two identical passport-style photographs",
            "Copy of government-issued identity document with photograph",
            "Copy of birth certificate with English translation if applicable",
            "Inspection, admission, or parole documentation (e.g., Form I-94 or entry stamp)"
        ]
    elif fid == "I-864":
        return [
            "Federal income tax return transcripts for the most recent tax year",
            "Form W-2 or 1099 wage statements",
            "Proof of sponsor's U.S. citizenship or lawful permanent resident status",
            "Evidence of current employment or self-employment"
        ]
    elif fid == "N-400":
        return [
            "Copy of Permanent Resident Card (Form I-551, front and back)",
            "Marriage certificate and proof of spouse's U.S. citizenship (if applying based on marriage)",
            "Tax transcripts proving continuous residence and physical presence",
            "Certified court dispositions for any arrests or legal citations"
        ]
    elif fid == "I-765":
        return [
            "Copy of Form I-94, passport, or previous employment authorization card",
            "Two identical 2x2 inch passport-style color photographs",
            "Evidence of eligibility under qualifying classification category"
        ]
    elif series == "I":
        return [
            "Government-issued photo identification",
            "Proof of lawful immigration status or inspection document (Form I-94)",
            "Petition-specific eligibility and supporting relational/financial documentation"
        ]
    elif series == "N":
        return [
            "Permanent Resident Card (Green Card) copy",
            "Continuous residence and physical presence supporting documents",
            "Civil and identity records relevant to naturalization claim"
        ]
    elif series == "G":
        return [
            "Identity verification and representation authorization credentials",
            "Relevant case or application receipt notice numbers"
        ]
    else:
        return [
            "Official government identification document",
            "Form-specific supporting evidence cited in published instructions"
        ]

guidance_list = []
for form in manifest["forms"]:
    fid = form["formId"]
    fee_cents = KNOWN_UNIFORM_FEES.get(fid, None)
    fee_notes = "Official statutory fee of ${:,.2f}".format(fee_cents / 100) if fee_cents is not None else "Fee varies by filing channel, applicant category, or fee reduction status; consult official Form G-1055 fee schedule."
    
    guidance_list.append({
        "formId": fid,
        "formNumber": form["formNumber"],
        "authority": form.get("issuer", "USCIS"),
        "officialInstructionsUrl": form["instructionsUrl"] or form.get("sourceUrl"),
        "feeScheduleCitationUrl": "https://www.uscis.gov/g-1055",
        "feeUsdCents": fee_cents,
        "feeNotes": fee_notes,
        "evidenceChecklist": make_evidence_checklist(form),
        "institutionGuidanceNotes": "Ensure edition date matches official USCIS edition requirements before filing."
    })

# Add preserved non-USCIS definitions
for preserved in manifest.get("preservedNonUscisDefinitions", []):
    guidance_list.append({
        "formId": preserved["documentId"],
        "formNumber": preserved["documentId"],
        "authority": preserved["issuer"],
        "officialInstructionsUrl": preserved["sourceUrl"],
        "feeScheduleCitationUrl": preserved["sourceUrl"],
        "feeUsdCents": None,
        "feeNotes": "Agency or institutional fee schedule applies; see official source.",
        "evidenceChecklist": [
            "Government-issued identity documentation",
            "Supporting institutional or program qualification evidence"
        ],
        "institutionGuidanceNotes": "Institutional non-USCIS workflow."
    })

output_doc = {
    "$schema": "./schemas/document-guidance.schema.json",
    "schemaVersion": "1.0.0",
    "authority": "USCIS",
    "lastVerifiedAt": "2026-09-14T00:00:00Z",
    "guidance": guidance_list
}

with open(GUIDANCE_PATH, "w", encoding="utf-8") as f:
    json.dump(output_doc, f, indent=2)

print(f"Generated guidance for {len(guidance_list)} documents at {GUIDANCE_PATH}")
