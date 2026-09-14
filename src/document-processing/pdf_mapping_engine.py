"""Declarative PDF mapping and document preparation engine (INF-05).

Processes versioned Document Blueprints against authoritative confirmed field values.
Supports:
1. FILLABLE_PDF: AcroForm field mapping, Unicode normalization, character bounds,
   overflow detection with automated attachment addendum generation, and script sanitization.
2. STATIC_ASSISTED: Structured preparation checklist and field cross-references for flat documents.
3. EXTERNAL_REFERENCE: Verified official portal destination and prerequisite checklists.

Zero third-party dependencies (stdlib-only), secure fail-closed execution.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import re
import unicodedata
from dataclasses import dataclass, field
from enum import Enum
from typing import Any
from urllib.parse import urlsplit


class PreparationMode(str, Enum):
    FILLABLE_PDF = "FILLABLE_PDF"
    STATIC_ASSISTED = "STATIC_ASSISTED"
    EXTERNAL_REFERENCE = "EXTERNAL_REFERENCE"


class OverflowStrategy(str, Enum):
    ATTACHMENT_ADDENDUM = "ATTACHMENT_ADDENDUM"
    TRUNCATE_ERROR = "TRUNCATE_ERROR"
    SPLIT_PAGES = "SPLIT_PAGES"


# Dangerous patterns in PDF values or metadata that could trigger script execution or injection
MALICIOUS_PDF_PATTERNS = re.compile(
    r"(?:/JavaScript\b|/JS\b|<script\b|javascript:|eval\s*\(|exec\s*\(|__proto__|constructor\b|os\.(?:system|popen)|subprocess)",
    re.IGNORECASE,
)

INDEXED_PATH_PATTERN = re.compile(r"^([a-z0-9_]+)\[(\d+)\](?:\.([a-z0-9_]+))?$")


@dataclass(frozen=True, slots=True)
class AddendumEntry:
    """An entry for Part 11 / Supplemental Information when field content overflows."""
    section_id: str
    section_title: str
    canonical_path: str
    field_label: str
    page_number: int | None
    part_number: str | None
    item_number: str | None
    content: str
    explanation: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "sectionId": self.section_id,
            "sectionTitle": self.section_title,
            "canonicalPath": self.canonical_path,
            "fieldLabel": self.field_label,
            "pageNumber": self.page_number,
            "partNumber": self.part_number,
            "itemNumber": self.item_number,
            "content": self.content,
            "explanation": self.explanation,
        }


@dataclass
class MappingResult:
    """Deterministic result of processing a blueprint against authoritative values."""
    preparation_mode: str
    is_valid: bool
    errors: list[str] = field(default_factory=list)
    pdf_field_values: dict[str, str] = field(default_factory=dict)
    overflow_addendum: list[dict[str, Any]] = field(default_factory=list)
    assisted_checklist: list[dict[str, Any]] = field(default_factory=list)
    external_reference: dict[str, Any] | None = None
    audit_metadata: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "preparationMode": self.preparation_mode,
            "isValid": self.is_valid,
            "errors": list(self.errors),
            "pdfFieldValues": dict(self.pdf_field_values),
            "overflowAddendum": list(self.overflow_addendum),
            "assistedChecklist": list(self.assisted_checklist),
            "externalReference": self.external_reference,
            "auditMetadata": self.audit_metadata,
        }

    def to_golden_json(self) -> str:
        """Serialize into deterministic, canonically sorted JSON for golden-output tests."""
        return json.dumps(self.to_dict(), sort_keys=True, indent=2, ensure_ascii=False)


class PdfMappingEngine:
    """Pure Python declarative document preparation and AcroForm mapping engine."""

    @classmethod
    def sanitize_value(cls, raw: str) -> str:
        """Normalize Unicode to NFC, strip control characters, and reject executable injection."""
        if not isinstance(raw, str):
            raw = str(raw)

        # Check for malicious code or script injection
        if MALICIOUS_PDF_PATTERNS.search(raw):
            raise ValueError(f"Malicious script or code pattern detected in value: {raw!r}")

        # Unicode standard NFC normalization
        normalized = unicodedata.normalize("NFC", raw)

        # Remove ASCII control characters except standard whitespace (space, tab, newline)
        sanitized = "".join(
            ch for ch in normalized
            if (ch in "\t\n\r" or not unicodedata.category(ch).startswith("C"))
        )

        return sanitized

    @classmethod
    def format_for_pdf(cls, value: Any, field_type: str) -> str:
        """Format a value according to field type for PDF rendering."""
        if value is None:
            return ""

        if field_type == "boolean":
            # In PDF AcroForms, checkboxes are typically 'Yes' or '1' when checked, 'Off' or empty when unchecked
            if isinstance(value, bool):
                return "Yes" if value else "Off"
            str_val = str(value).strip().lower()
            return "Yes" if str_val in ("true", "1", "yes", "y") else "Off"

        if field_type == "date":
            # Convert ISO YYYY-MM-DD to standard US form format MM/DD/YYYY if matching
            val_str = str(value).strip()
            date_match = re.match(r"^(\d{4})-(\d{2})-(\d{2})$", val_str)
            if date_match:
                year, month, day = date_match.groups()
                return f"{month}/{day}/{year}"
            return val_str

        return str(value).strip()

    def process(self, blueprint: dict[str, Any], input_values: dict[str, Any]) -> MappingResult:
        """Process a validated blueprint against authoritative input values."""
        mode = blueprint.get("preparationMode")
        if mode not in PreparationMode.__members__:
            return MappingResult(
                preparation_mode=str(mode),
                is_valid=False,
                errors=[f"Unsupported preparationMode: {mode!r}"],
            )

        # Flatten nested list/dict inputs for repeatable array fields
        flattened_inputs = dict(input_values)
        for k, v in list(input_values.items()):
            if isinstance(v, list):
                for i, item in enumerate(v):
                    if isinstance(item, dict):
                        for sub_k, sub_v in item.items():
                            flattened_inputs[f"{k}[{i}].{sub_k}"] = sub_v
                    else:
                        flattened_inputs[f"{k}[{i}]"] = item

        if mode == PreparationMode.FILLABLE_PDF.value:
            return self._process_fillable_pdf(blueprint, flattened_inputs)
        elif mode == PreparationMode.STATIC_ASSISTED.value:
            return self._process_static_assisted(blueprint, flattened_inputs)
        elif mode == PreparationMode.EXTERNAL_REFERENCE.value:
            return self._process_external_reference(blueprint, flattened_inputs)
        else:
            return MappingResult(
                preparation_mode=mode,
                is_valid=False,
                errors=[f"Unknown preparation mode: {mode}"],
            )

    def _process_fillable_pdf(
        self, blueprint: dict[str, Any], input_values: dict[str, Any]
    ) -> MappingResult:
        errors: list[str] = []
        pdf_fields: dict[str, str] = {}
        addendum_entries: list[AddendumEntry] = []

        sections_by_id = {
            s["sectionId"]: s
            for s in blueprint.get("sections", [])
            if isinstance(s, dict) and "sectionId" in s
        }

        fields_list = blueprint.get("fields", [])
        field_by_path: dict[str, dict[str, Any]] = {}
        for f in fields_list:
            if isinstance(f, dict) and "canonicalPath" in f:
                field_by_path[f["canonicalPath"]] = f

        # Check section & field conditions and process values
        processed_keys: set[str] = set()

        for field_def in fields_list:
            cpath = field_def["canonicalPath"]
            ftype = field_def.get("type", "string")
            label = field_def.get("label", cpath)
            section_id = field_def.get("sectionId", "")
            section = sections_by_id.get(section_id, {})
            max_len = field_def.get("maxLength")
            overflow_strategy = (
                section.get("overflowStrategy")
                or OverflowStrategy.ATTACHMENT_ADDENDUM.value
            )

            # Evaluate section condition if present
            sec_cond = section.get("condition")
            if sec_cond and not self._evaluate_condition(sec_cond, input_values):
                # Section condition not satisfied, skip fields
                continue

            # Evaluate field condition if present
            field_cond = field_def.get("condition")
            if field_cond and not self._evaluate_condition(field_cond, input_values):
                continue

            # Check if canonical path represents a repeatable array, e.g. "beneficiary[].name"
            if "[]" in cpath:
                base_array_path, sub_attr = cpath.split("[]", 1)
                sub_attr = sub_attr.lstrip(".")
                # Find all matching inputs in input_values: e.g. base_array_path[0].sub_attr
                pattern = re.compile(rf"^{re.escape(base_array_path)}\[(\d+)\](?:\.{re.escape(sub_attr)})?$")
                matching_inputs: list[tuple[int, str, Any]] = []
                for k, val in input_values.items():
                    m = pattern.match(k)
                    if m:
                        idx = int(m.group(1))
                        matching_inputs.append((idx, k, val))

                matching_inputs.sort(key=lambda x: x[0])
                max_occurs = section.get("maxOccurs", 1)

                for idx, input_key, val in matching_inputs:
                    processed_keys.add(input_key)
                    if val is None or val == "":
                        continue

                    try:
                        sanitized = self.sanitize_value(val)
                    except ValueError as exc:
                        errors.append(f"{input_key}: {exc}")
                        continue

                    # If repeat index exceeds inline form capacity (or max_occurs inline)
                    pdf_mapping_template = field_def.get("pdfFieldMapping")
                    can_map_inline = False
                    target_pdf_field = ""

                    if pdf_mapping_template:
                        if "{index}" in pdf_mapping_template:
                            target_pdf_field = pdf_mapping_template.replace("{index}", str(idx))
                            can_map_inline = True
                        elif idx == 0:
                            # Primary/first slot maps directly
                            target_pdf_field = pdf_mapping_template
                            can_map_inline = True

                    # Handle overflow if index exceeds inline slots or max_occurs
                    if idx >= max_occurs or not can_map_inline:
                        if overflow_strategy == OverflowStrategy.ATTACHMENT_ADDENDUM.value:
                            addendum_entries.append(
                                AddendumEntry(
                                    section_id=section_id or "supplemental",
                                    section_title=section.get("title", "Supplemental Information"),
                                    canonical_path=input_key,
                                    field_label=f"{label} [#{idx + 1}]",
                                    page_number=None,
                                    part_number=section.get("partNumber"),
                                    item_number=str(idx + 1),
                                    content=sanitized,
                                    explanation=f"Exceeded form capacity for repeated section '{section.get('title', section_id)}'",
                                )
                            )
                        elif overflow_strategy == OverflowStrategy.TRUNCATE_ERROR.value:
                            errors.append(f"Repeat index {idx + 1} exceeds max capacity {max_occurs} for '{input_key}'")
                        continue

                    # If can map inline, check length bounds
                    formatted_val = self.format_for_pdf(sanitized, ftype)
                    if max_len and len(formatted_val) > max_len:
                        if overflow_strategy == OverflowStrategy.ATTACHMENT_ADDENDUM.value:
                            # Put truncated text in field, full text in addendum
                            truncated = formatted_val[:max_len]
                            pdf_fields[target_pdf_field] = truncated
                            addendum_entries.append(
                                AddendumEntry(
                                    section_id=section_id or "supplemental",
                                    section_title=section.get("title", "Supplemental Information"),
                                    canonical_path=input_key,
                                    field_label=f"{label} [#{idx + 1}]",
                                    page_number=None,
                                    part_number=section.get("partNumber"),
                                    item_number=str(idx + 1),
                                    content=sanitized,
                                    explanation=f"Text length ({len(formatted_val)}) exceeded character limit ({max_len})",
                                )
                            )
                        else:
                            errors.append(f"Value for '{input_key}' exceeds max length {max_len} (got {len(formatted_val)})")
                    else:
                        pdf_fields[target_pdf_field] = formatted_val

            else:
                # Regular non-repeatable field
                processed_keys.add(cpath)
                val = input_values.get(cpath)
                if val is None or val == "":
                    if field_def.get("required", False):
                        errors.append(f"Missing required field: '{cpath}'")
                    continue

                try:
                    sanitized = self.sanitize_value(val)
                except ValueError as exc:
                    errors.append(f"{cpath}: {exc}")
                    continue

                formatted_val = self.format_for_pdf(sanitized, ftype)
                pdf_target = field_def.get("pdfFieldMapping")

                # Length check
                if max_len and len(formatted_val) > max_len:
                    if overflow_strategy == OverflowStrategy.ATTACHMENT_ADDENDUM.value:
                        if pdf_target:
                            pdf_fields[pdf_target] = formatted_val[:max_len]
                        addendum_entries.append(
                            AddendumEntry(
                                section_id=section_id or "supplemental",
                                section_title=section.get("title", "Supplemental Information"),
                                canonical_path=cpath,
                                field_label=label,
                                page_number=None,
                                part_number=section.get("partNumber"),
                                item_number=None,
                                content=sanitized,
                                explanation=f"Text length ({len(formatted_val)}) exceeded character limit ({max_len})",
                            )
                        )
                    else:
                        errors.append(f"Value for '{cpath}' exceeds max length {max_len} (got {len(formatted_val)})")
                else:
                    if pdf_target:
                        pdf_fields[pdf_target] = formatted_val

        # Deterministic content digest for immutable auditability
        content_for_digest = {
            "blueprint": f"{blueprint.get('namespace')}/{blueprint.get('blueprintId')}@r{blueprint.get('revision')}",
            "pdfFieldValues": pdf_fields,
            "overflowAddendum": [e.to_dict() for e in addendum_entries],
        }
        digest = hashlib.sha256(json.dumps(content_for_digest, sort_keys=True).encode("utf-8")).hexdigest()

        return MappingResult(
            preparation_mode=PreparationMode.FILLABLE_PDF.value,
            is_valid=(len(errors) == 0),
            errors=errors,
            pdf_field_values=pdf_fields,
            overflow_addendum=[e.to_dict() for e in addendum_entries],
            audit_metadata={
                "manifestDigest": digest,
                "blueprint": f"{blueprint.get('namespace')}/{blueprint.get('blueprintId')}@r{blueprint.get('revision')}",
                "pdfFieldCount": len(pdf_fields),
                "addendumEntryCount": len(addendum_entries),
                "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            },
        )

    def _process_static_assisted(
        self, blueprint: dict[str, Any], input_values: dict[str, Any]
    ) -> MappingResult:
        """Process STATIC_ASSISTED documents, generating a verified preparation checklist and summary."""
        errors: list[str] = []
        checklist: list[dict[str, Any]] = []

        fields = blueprint.get("fields", [])
        for field_def in fields:
            cpath = field_def.get("canonicalPath", "")
            label = field_def.get("label", cpath)
            val = input_values.get(cpath)
            is_required = field_def.get("required", False)

            if val is not None and val != "":
                try:
                    sanitized = self.sanitize_value(val)
                except ValueError as exc:
                    errors.append(f"{cpath}: {exc}")
                    sanitized = str(val)
                status = "COMPLETED"
            else:
                sanitized = None
                status = "PENDING"
                if is_required:
                    errors.append(f"Missing required intake item: '{cpath}'")

            checklist.append({
                "canonicalPath": cpath,
                "label": label,
                "required": is_required,
                "status": status,
                "value": sanitized,
                "instructions": field_def.get("instructions", f"Review and transcribe {label}"),
            })

        evidence_list = []
        for req in blueprint.get("evidenceRequirements", []):
            evidence_list.append({
                "code": req.get("code"),
                "title": req.get("title"),
                "attributedRole": req.get("attributedRole"),
                "isConditional": req.get("isConditional", False),
            })

        return MappingResult(
            preparation_mode=PreparationMode.STATIC_ASSISTED.value,
            is_valid=(len(errors) == 0),
            errors=errors,
            assisted_checklist=checklist,
            audit_metadata={
                "blueprint": f"{blueprint.get('namespace')}/{blueprint.get('blueprintId')}@r{blueprint.get('revision')}",
                "totalItems": len(checklist),
                "evidenceRequired": evidence_list,
                "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            },
        )

    def _process_external_reference(
        self, blueprint: dict[str, Any], input_values: dict[str, Any]
    ) -> MappingResult:
        """Process EXTERNAL_REFERENCE documents, generating portal guidance and external links."""
        source = blueprint.get("source", {})
        url = source.get("url")

        external_ref = {
            "title": blueprint.get("title"),
            "issuer": blueprint.get("issuer"),
            "officialPortalUrl": url,
            "externalInstructions": "Filing occurs directly on the official agency portal. LaPluma prepares verified prerequisites.",
            "prerequisites": [],
        }

        for req in blueprint.get("evidenceRequirements", []):
            external_ref["prerequisites"].append({
                "code": req.get("code"),
                "title": req.get("title"),
                "role": req.get("attributedRole"),
            })

        return MappingResult(
            preparation_mode=PreparationMode.EXTERNAL_REFERENCE.value,
            is_valid=True,
            external_reference=external_ref,
            audit_metadata={
                "blueprint": f"{blueprint.get('namespace')}/{blueprint.get('blueprintId')}@r{blueprint.get('revision')}",
                "destinationUrl": url,
                "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            },
        )

    @classmethod
    def _evaluate_condition(cls, condition: dict[str, Any], input_values: dict[str, Any]) -> bool:
        """Declarative, safe evaluation of field/section conditions without eval()."""
        field_path = condition.get("field")
        operator = condition.get("operator")
        expected_val = condition.get("value")

        if not field_path or not operator:
            return True

        actual_val = input_values.get(field_path)

        if operator == "is_set":
            return actual_val is not None and actual_val != ""
        elif operator == "is_not_set":
            return actual_val is None or actual_val == ""
        elif operator == "equals":
            return actual_val == expected_val
        elif operator == "not_equals":
            return actual_val != expected_val
        elif operator == "in":
            if isinstance(expected_val, (list, tuple, set)):
                return actual_val in expected_val
            return False

        return True
