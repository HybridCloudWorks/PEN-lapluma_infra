#!/usr/bin/env python3
"""LaPluma Document Blueprint Onboarding and Validation CLI (INF-17).

Provides declarative initialization, schema and synthetic fixture validation,
and immutable packaging for versioned LaPluma Document Blueprints.

Zero third-party dependencies required (standard-library only), with optional
jsonschema acceleration if installed.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SCHEMA_PATH = ROOT / "contracts/schemas/document-blueprint.schema.json"

IDENTIFIER_PATTERN = re.compile(r"^[a-z0-9_-]{2,64}$")
CANONICAL_PATH_PATTERN = re.compile(r"^[a-z0-9_]+(\[\])?(\.[a-z0-9_]+(\[\])?)*$")
SHA256_PATTERN = re.compile(r"^[a-f0-9]{64}$")
DATE_PATTERN = re.compile(r"^\d{4}-\d{2}-\d{2}$")

VALID_PREPARATION_MODES = frozenset({"FILLABLE_PDF", "STATIC_ASSISTED", "EXTERNAL_REFERENCE"})
VALID_ARTIFACT_TYPES = frozenset({"OFFICIAL_PDF", "XFA", "FLAT", "EXTERNAL_LINK", "AUTHORED_TEMPLATE"})
VALID_ACCESS_SCOPES = frozenset({"SHARED_OFFICIAL", "INSTITUTION_PRIVATE"})
VALID_FIELD_TYPES = frozenset({"string", "date", "boolean", "choice", "signature", "number"})
VALID_RULE_TYPES = frozenset({"regex", "range", "cross_field", "custom_bounded", "conditional_required"})
VALID_OVERFLOW_STRATEGIES = frozenset({"ATTACHMENT_ADDENDUM", "TRUNCATE_ERROR", "SPLIT_PAGES"})
VALID_CONDITION_OPERATORS = frozenset({"equals", "not_equals", "is_set", "is_not_set", "in"})
VALID_CHARACTER_SETS = frozenset({"ASCII", "UNICODE_STANDARD", "NUMERIC", "ALPHA_NUMERIC"})

EXECUTABLE_CODE_PATTERN = re.compile(
    r"(?:<\s*script\b|javascript:|eval\s*\(|exec\s*\(|__import__|__proto__|constructor\b|os\.(?:system|popen)|subprocess\b|;\s*drop\s+table)",
    re.IGNORECASE,
)
UNSAFE_HOST_PATTERN = re.compile(
    r"^(?:localhost|127\.\d+\.\d+\.\d+|0\.0\.0\.0|169\.254\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(?:1[6-9]|2\d|3[01])\.\d+\.\d+|192\.168\.\d+\.\d+|metadata\.google\.internal)$",
    re.IGNORECASE,
)
OFFICIAL_NAMESPACES = frozenset({"uscis", "irs", "dos", "eoir", "dhs", "cbp", "ice"})


class BlueprintValidationError(Exception):
    """Raised when a blueprint violates data contract or fixture validation rules."""


class BlueprintValidator:
    """Deterministic, dependency-free validator for LaPluma Document Blueprints."""

    def __init__(self, schema_path: Path | None = None) -> None:
        self.schema_path = schema_path or DEFAULT_SCHEMA_PATH
        self._schema_data: dict[str, Any] | None = None

    @property
    def schema(self) -> dict[str, Any]:
        if self._schema_data is None:
            if not self.schema_path.is_file():
                raise FileNotFoundError(f"Blueprint schema not found at {self.schema_path}")
            self._schema_data = json.loads(self.schema_path.read_text(encoding="utf-8"))
        return self._schema_data

    def validate_file(self, blueprint_file: Path) -> list[str]:
        """Validate a blueprint JSON file, returning a list of validation failure messages."""
        if not blueprint_file.is_file():
            return [f"File not found: {blueprint_file}"]

        try:
            content = blueprint_file.read_text(encoding="utf-8")
            data = json.loads(content)
        except json.JSONDecodeError as exc:
            return [f"Malformed JSON in {blueprint_file}: {exc}"]
        except Exception as exc:
            return [f"Failed to read {blueprint_file}: {exc}"]

        return self.validate_data(data, source_name=str(blueprint_file))

    def validate_data(self, data: Any, source_name: str = "<blueprint>") -> list[str]:
        """Validate blueprint dictionary against data contracts and evaluate synthetic fixtures."""
        failures: list[str] = []

        if not isinstance(data, dict):
            return [f"{source_name}: Root must be a JSON object"]

        # Security check: scan for executable code or script injection anywhere in structure
        failures.extend(self._scan_for_injection(data, "root", source_name))

        # Check against jsonschema if installed
        try:
            import jsonschema
            validator = jsonschema.Draft202012Validator(self.schema)
            for err in validator.iter_errors(data):
                loc = " -> ".join(str(p) for p in err.path) or "root"
                failures.append(f"{source_name} [{loc}]: {err.message}")
        except ImportError:
            # Fallback to robust built-in validation
            failures.extend(self._builtin_schema_check(data, source_name))

        # Perform additional semantic checks and fixture evaluations
        failures.extend(self._semantic_checks(data, source_name))
        failures.extend(self._evaluate_synthetic_fixtures(data, source_name))

        return failures

    def _scan_for_injection(self, val: Any, loc: str, src: str) -> list[str]:
        failures: list[str] = []
        if isinstance(val, str):
            if EXECUTABLE_CODE_PATTERN.search(val):
                failures.append(f"{src} [{loc}]: Executable code or injection pattern rejected in value: {val!r}")
        elif isinstance(val, dict):
            for k, v in val.items():
                if EXECUTABLE_CODE_PATTERN.search(str(k)):
                    failures.append(f"{src} [{loc}]: Injection pattern rejected in property name: {k!r}")
                failures.extend(self._scan_for_injection(v, f"{loc}.{k}", src))
        elif isinstance(val, list):
            for i, item in enumerate(val):
                failures.extend(self._scan_for_injection(item, f"{loc}[{i}]", src))
        return failures

    def _builtin_schema_check(self, data: dict[str, Any], src: str) -> list[str]:
        failures: list[str] = []
        required_fields = [
            "namespace", "blueprintId", "revision", "title", "issuer",
            "preparationMode", "artifactType", "accessScope", "fields",
            "evidenceRequirements"
        ]
        for field in required_fields:
            if field not in data:
                failures.append(f"{src}: Missing required property '{field}'")

        # Check allowed top-level keys
        allowed_keys = {
            "$schema", "namespace", "blueprintId", "revision", "title", "issuer",
            "officialEditionDate", "preparationMode", "artifactType", "accessScope",
            "source", "sections", "fields", "validationRules", "evidenceRequirements", "syntheticFixtures"
        }
        for key in data:
            if key not in allowed_keys:
                failures.append(f"{src}: Unexpected extra property '{key}' (additionalProperties: false)")

        # Validate identifiers
        for id_field in ("namespace", "blueprintId"):
            val = data.get(id_field)
            if val is not None:
                if not isinstance(val, str) or not IDENTIFIER_PATTERN.match(val):
                    failures.append(f"{src}: '{id_field}' must match ^[a-z0-9_-]{{2,64}}$ (got {val!r})")

        # Validate revision
        rev = data.get("revision")
        if rev is not None and (not isinstance(rev, int) or rev < 1):
            failures.append(f"{src}: 'revision' must be an integer >= 1 (got {rev!r})")

        # Validate preparationMode
        mode = data.get("preparationMode")
        if mode is not None and mode not in VALID_PREPARATION_MODES:
            failures.append(f"{src}: 'preparationMode' must be one of {sorted(VALID_PREPARATION_MODES)} (got {mode!r})")

        # Validate artifactType
        art = data.get("artifactType")
        if art is not None and art not in VALID_ARTIFACT_TYPES:
            failures.append(f"{src}: 'artifactType' must be one of {sorted(VALID_ARTIFACT_TYPES)} (got {art!r})")

        # Validate accessScope
        scope = data.get("accessScope")
        if scope is not None and scope not in VALID_ACCESS_SCOPES:
            failures.append(f"{src}: 'accessScope' must be one of {sorted(VALID_ACCESS_SCOPES)} (got {scope!r})")

        # Validate officialEditionDate
        date_val = data.get("officialEditionDate")
        if date_val is not None:
            if not isinstance(date_val, str) or not DATE_PATTERN.match(date_val):
                failures.append(f"{src}: 'officialEditionDate' must match YYYY-MM-DD (got {date_val!r})")

        # Validate source
        source = data.get("source")
        if source is not None:
            if not isinstance(source, dict):
                failures.append(f"{src}: 'source' must be an object")
            else:
                sha = source.get("sha256")
                if sha is not None and (not isinstance(sha, str) or not SHA256_PATTERN.match(sha)):
                    failures.append(f"{src}: 'source.sha256' must be 64-char lowercase hex (got {sha!r})")

        return failures

    def _validate_condition(self, cond: Any, loc: str, src: str) -> list[str]:
        failures: list[str] = []
        if not isinstance(cond, dict):
            return [f"{src}: {loc} must be an object"]
        fpath = cond.get("field")
        if not fpath or not isinstance(fpath, str):
            failures.append(f"{src}: {loc}.field must be a non-empty string")
        op = cond.get("operator")
        if op not in VALID_CONDITION_OPERATORS:
            failures.append(f"{src}: {loc}.operator must be one of {sorted(VALID_CONDITION_OPERATORS)} (got {op!r})")
        return failures

    def _semantic_checks(self, data: dict[str, Any], src: str) -> list[str]:
        failures: list[str] = []

        # Source URL security check (reject http, loopback, private IP, metadata)
        source = data.get("source")
        if isinstance(source, dict):
            url = source.get("url")
            if url is not None:
                if not isinstance(url, str):
                    failures.append(f"{src}: source.url must be a string")
                else:
                    from urllib.parse import urlsplit
                    parts = urlsplit(url)
                    if parts.scheme != "https":
                        failures.append(f"{src}: source.url must use https scheme (got {parts.scheme!r})")
                    if parts.username or parts.password:
                        failures.append(f"{src}: source.url must not embed credentials")
                    host = (parts.hostname or "").lower()
                    if not host or UNSAFE_HOST_PATTERN.match(host):
                        failures.append(f"{src}: source.url references unsafe or private host: {host!r}")

        # Institutional scope invariant: cannot customize official namespaces
        if data.get("accessScope") == "INSTITUTION_PRIVATE":
            ns = data.get("namespace", "")
            if ns in OFFICIAL_NAMESPACES:
                failures.append(
                    f"{src}: INSTITUTION_PRIVATE blueprint cannot redefine reserved official agency namespace '{ns}'"
                )

        # Validate sections if defined
        sections = data.get("sections", [])
        section_ids: set[str] = set()
        if sections is not None:
            if not isinstance(sections, list):
                failures.append(f"{src}: 'sections' must be a list")
            else:
                for idx, sec in enumerate(sections):
                    if not isinstance(sec, dict):
                        failures.append(f"{src}: sections[{idx}] must be an object")
                        continue
                    sid = sec.get("sectionId")
                    if not sid or not isinstance(sid, str) or not IDENTIFIER_PATTERN.match(sid):
                        failures.append(f"{src}: sections[{idx}].sectionId must match ^[a-z0-9_-]{{2,64}}$ (got {sid!r})")
                    elif sid in section_ids:
                        failures.append(f"{src}: Duplicate sectionId '{sid}' in sections[{idx}]")
                    else:
                        section_ids.add(sid)

                    stitle = sec.get("title")
                    if not stitle or not isinstance(stitle, str):
                        failures.append(f"{src}: sections[{idx}].title must be a non-empty string")

                    ov = sec.get("overflowStrategy")
                    if ov is not None and ov not in VALID_OVERFLOW_STRATEGIES:
                        failures.append(f"{src}: sections[{idx}].overflowStrategy must be one of {sorted(VALID_OVERFLOW_STRATEGIES)}")

                    max_occ = sec.get("maxOccurs")
                    if max_occ is not None and (not isinstance(max_occ, int) or max_occ < 1):
                        failures.append(f"{src}: sections[{idx}].maxOccurs must be an integer >= 1")

                    cond = sec.get("condition")
                    if cond is not None:
                        failures.extend(self._validate_condition(cond, f"sections[{idx}].condition", src))

        # Validate fields
        fields = data.get("fields")
        field_paths: set[str] = set()

        if isinstance(fields, list):
            for idx, field in enumerate(fields):
                if not isinstance(field, dict):
                    failures.append(f"{src}: fields[{idx}] must be an object")
                    continue
                path = field.get("canonicalPath")
                if not path or not isinstance(path, str) or not CANONICAL_PATH_PATTERN.match(path):
                    failures.append(f"{src}: fields[{idx}].canonicalPath must match ^[a-z0-9_]+(\\[\\])?(\\.[a-z0-9_]+(\\[\\])?)*$ (got {path!r})")
                elif path in field_paths:
                    failures.append(f"{src}: Duplicate canonicalPath '{path}' in fields[{idx}]")
                else:
                    field_paths.add(path)

                ftype = field.get("type")
                if ftype not in VALID_FIELD_TYPES:
                    failures.append(f"{src}: fields[{idx}].type must be one of {sorted(VALID_FIELD_TYPES)} (got {ftype!r})")

                sec_id = field.get("sectionId")
                if sec_id is not None and sec_id not in section_ids:
                    failures.append(f"{src}: fields[{idx}].sectionId '{sec_id}' does not exist in declared sections")

                max_len = field.get("maxLength")
                if max_len is not None and (not isinstance(max_len, int) or max_len < 1):
                    failures.append(f"{src}: fields[{idx}].maxLength must be an integer >= 1")

                cset = field.get("characterSet")
                if cset is not None and cset not in VALID_CHARACTER_SETS:
                    failures.append(f"{src}: fields[{idx}].characterSet must be one of {sorted(VALID_CHARACTER_SETS)}")

                choices = field.get("choices")
                if choices is not None:
                    if not isinstance(choices, list) or not all(isinstance(c, str) and c for c in choices):
                        failures.append(f"{src}: fields[{idx}].choices must be a list of non-empty strings")

                fcond = field.get("condition")
                if fcond is not None:
                    failures.extend(self._validate_condition(fcond, f"fields[{idx}].condition", src))

        # Validate validation rules reference existing fields and adhere to strict DSL
        rules = data.get("validationRules", [])
        if isinstance(rules, list):
            for idx, rule in enumerate(rules):
                if not isinstance(rule, dict):
                    failures.append(f"{src}: validationRules[{idx}] must be an object")
                    continue
                rtype = rule.get("type")
                if rtype not in VALID_RULE_TYPES:
                    failures.append(f"{src}: validationRules[{idx}].type must be one of {sorted(VALID_RULE_TYPES)}")
                expr = rule.get("expression", "")
                if not expr or not isinstance(expr, str):
                    failures.append(f"{src}: validationRules[{idx}].expression must be a non-empty string")
                    continue

                # Enforce bounded DSL grammar, rejecting arbitrary expressions
                if rtype == "regex":
                    if ":" not in expr:
                        failures.append(f"{src}: validationRules[{idx}] regex expression must be 'canonicalPath:pattern'")
                    else:
                        p, pat = expr.split(":", 1)
                        try:
                            re.compile(pat)
                        except re.error as err:
                            failures.append(f"{src}: validationRules[{idx}] invalid regex pattern: {err}")
                elif rtype == "range":
                    if expr.count(":") != 2:
                        failures.append(f"{src}: validationRules[{idx}] range expression must be 'canonicalPath:min:max'")
                elif rtype == "cross_field":
                    if "==" not in expr and "!=" not in expr:
                        failures.append(f"{src}: validationRules[{idx}] cross_field expression must be 'fieldA==fieldB' or 'fieldA!=fieldB'")
                elif rtype == "conditional_required":
                    if not (expr.startswith("when:") and ":then_required:" in expr):
                        failures.append(f"{src}: validationRules[{idx}] conditional_required expression must be 'when:path==val:then_required:targetPath'")

        return failures

    def _evaluate_synthetic_fixtures(self, data: dict[str, Any], src: str) -> list[str]:
        failures: list[str] = []
        fixtures = data.get("syntheticFixtures")
        if not fixtures:
            return failures

        if not isinstance(fixtures, list):
            return [f"{src}: 'syntheticFixtures' must be a list"]

        fields = {f["canonicalPath"]: f for f in data.get("fields", []) if isinstance(f, dict) and "canonicalPath" in f}
        sections = {s["sectionId"]: s for s in data.get("sections", []) if isinstance(s, dict) and "sectionId" in s}
        rules = [r for r in data.get("validationRules", []) if isinstance(r, dict)]

        for idx, fixture in enumerate(fixtures):
            if not isinstance(fixture, dict):
                failures.append(f"{src}: syntheticFixtures[{idx}] must be an object")
                continue

            fixture_name = fixture.get("fixtureName", f"fixture_{idx}")
            input_values = fixture.get("inputValues")
            expected_valid = fixture.get("expectedValid", True)

            if not isinstance(input_values, dict):
                failures.append(f"{src}: syntheticFixtures[{idx}] ({fixture_name}) 'inputValues' must be an object")
                continue

            # Evaluate validity of input_values against required fields, sections, constraints, and rules
            is_valid, reason = self._check_fixture_validity(input_values, fields, sections, rules)

            if is_valid != expected_valid:
                failures.append(
                    f"{src}: Synthetic fixture '{fixture_name}' expectation mismatch: "
                    f"expected valid={expected_valid}, but evaluation was valid={is_valid} ({reason})"
                )

        return failures

    @classmethod
    def _is_condition_met(cls, cond: dict[str, Any], input_values: dict[str, Any]) -> bool:
        field_path = cond.get("field")
        op = cond.get("operator")
        val = cond.get("value")
        if not field_path or not op:
            return True
        actual = input_values.get(field_path)
        if op == "is_set":
            return actual is not None and actual != ""
        elif op == "is_not_set":
            return actual is None or actual == ""
        elif op == "equals":
            return actual == val
        elif op == "not_equals":
            return actual != val
        elif op == "in":
            return isinstance(val, (list, tuple, set)) and actual in val
        return True

    def _check_fixture_validity(
        self,
        input_values: dict[str, Any],
        fields: dict[str, dict[str, Any]],
        sections: dict[str, dict[str, Any]],
        rules: list[dict[str, Any]]
    ) -> tuple[bool, str]:
        # 1. Check required fields and field constraints (accounting for conditional activation)
        for path, field in fields.items():
            # Check section condition if field belongs to a section
            sec_id = field.get("sectionId")
            if sec_id and sec_id in sections:
                sec_cond = sections[sec_id].get("condition")
                if sec_cond and not self._is_condition_met(sec_cond, input_values):
                    continue

            # Check field condition
            fcond = field.get("condition")
            if fcond and not self._is_condition_met(fcond, input_values):
                continue

            val = input_values.get(path)

            if field.get("required", False):
                if val is None or val == "":
                    return False, f"Missing required field '{path}'"

            # Check choices
            choices = field.get("choices")
            if choices and val is not None and val != "":
                if val not in choices:
                    return False, f"Value '{val}' for field '{path}' not in allowed choices {choices}"

            # Check maxLength
            max_len = field.get("maxLength")
            if max_len and val is not None and isinstance(val, str) and len(val) > max_len:
                # If overflow strategy is ATTACHMENT_ADDENDUM, overflow is permitted and handled
                sec = sections.get(sec_id, {}) if sec_id else {}
                strat = sec.get("overflowStrategy")
                if strat != "ATTACHMENT_ADDENDUM":
                    return False, f"Field '{path}' exceeds max length {max_len} (got {len(val)})"

        # 2. Check validation rules
        for rule in rules:
            rtype = rule.get("type")
            expr = rule.get("expression", "")
            err_msg = rule.get("errorMessage", f"Violated rule {rule.get('ruleId')}")

            if rtype == "regex":
                parts = expr.split(":", 1)
                if len(parts) == 2:
                    path, pattern = parts[0].strip(), parts[1].strip()
                    val = input_values.get(path)
                    if val is not None and isinstance(val, str):
                        try:
                            if not re.search(pattern, val):
                                return False, f"{err_msg} (field '{path}' did not match pattern '{pattern}')"
                        except re.error as re_err:
                            return False, f"Invalid regex in rule {rule.get('ruleId')}: {re_err}"

            elif rtype == "range":
                parts = expr.split(":")
                if len(parts) == 3:
                    path, min_str, max_str = parts[0].strip(), parts[1].strip(), parts[2].strip()
                    val = input_values.get(path)
                    if val is not None:
                        if isinstance(val, (int, float)):
                            try:
                                min_val = float(min_str)
                                max_val = float(max_str)
                                if val < min_val or val > max_val:
                                    return False, f"{err_msg} (field '{path}' value {val} outside [{min_val}, {max_val}])"
                            except ValueError:
                                pass
                        elif isinstance(val, str):
                            if val < min_str or val > max_str:
                                return False, f"{err_msg} (field '{path}' value {val} outside [{min_str}, {max_str}])"

            elif rtype == "cross_field":
                if "==" in expr:
                    p1, p2 = [p.strip() for p in expr.split("==", 1)]
                    if input_values.get(p1) != input_values.get(p2):
                        return False, f"{err_msg} ({p1} != {p2})"
                elif "!=" in expr:
                    p1, p2 = [p.strip() for p in expr.split("!=", 1)]
                    if input_values.get(p1) == input_values.get(p2):
                        return False, f"{err_msg} ({p1} == {p2})"

            elif rtype == "conditional_required":
                # Expression format: "when:condPath==condVal:then_required:targetPath"
                parts = expr.split(":")
                if len(parts) >= 4 and parts[0] == "when" and parts[2] == "then_required":
                    cond_expr = parts[1].strip()
                    target_path = parts[3].strip()
                    if "==" in cond_expr:
                        cp, cv = [p.strip() for p in cond_expr.split("==", 1)]
                        if str(input_values.get(cp)) == cv:
                            if not input_values.get(target_path):
                                return False, f"{err_msg} ('{target_path}' is required when {cp}=={cv})"
                    elif "!=" in cond_expr:
                        cp, cv = [p.strip() for p in cond_expr.split("!=", 1)]
                        if str(input_values.get(cp)) != cv:
                            if not input_values.get(target_path):
                                return False, f"{err_msg} ('{target_path}' is required when {cp}!={cv})"

        return True, "All validation rules satisfied"


def cmd_init(args: argparse.Namespace) -> int:
    """Scaffold a new blueprint template."""
    out_path = Path(args.output)
    if out_path.is_dir():
        out_path = out_path / "blueprint.json"

    if out_path.exists() and not args.force:
        print(f"ERROR: Destination file {out_path} already exists. Use --force to overwrite.", file=sys.stderr)
        return 1

    blueprint = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "namespace": args.namespace,
        "blueprintId": args.id,
        "revision": args.revision,
        "title": args.title,
        "issuer": args.issuer,
        "officialEditionDate": args.edition_date or datetime.date.today().isoformat(),
        "preparationMode": args.mode,
        "artifactType": args.artifact_type,
        "accessScope": args.access_scope,
        "source": {
            "url": args.source_url or "https://example.gov/documents/form.pdf",
            "sha256": args.source_sha256 or "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "lastVerified": datetime.datetime.now(datetime.timezone.utc).isoformat()
        },
        "fields": [
            {
                "canonicalPath": "applicant.given_name",
                "type": "string",
                "label": "Given Name",
                "required": True,
                "attributedRole": "APPLICANT"
            },
            {
                "canonicalPath": "applicant.family_name",
                "type": "string",
                "label": "Family Name",
                "required": True,
                "attributedRole": "APPLICANT"
            }
        ],
        "validationRules": [
            {
                "ruleId": "applicant_name_regex",
                "type": "regex",
                "expression": "applicant.given_name:^[A-Za-z\\s\\-']+$",
                "errorMessage": "Applicant given name contains invalid characters"
            }
        ],
        "evidenceRequirements": [
            {
                "code": "GOV_ID",
                "title": "Government-Issued Photo Identification",
                "attributedRole": "APPLICANT",
                "isConditional": False,
                "acceptedMimeTypes": ["application/pdf", "image/jpeg", "image/png"]
            }
        ],
        "syntheticFixtures": [
            {
                "fixtureName": "standard_valid_applicant",
                "expectedValid": True,
                "inputValues": {
                    "applicant.given_name": "Jane",
                    "applicant.family_name": "Doe"
                }
            },
            {
                "fixtureName": "invalid_missing_surname",
                "expectedValid": False,
                "inputValues": {
                    "applicant.given_name": "Jane"
                }
            }
        ]
    }

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(blueprint, indent=2) + "\n", encoding="utf-8")
    print(f"Scaffolded blueprint created at {out_path}")
    return 0


def cmd_validate(args: argparse.Namespace) -> int:
    """Validate one or more blueprint files against schema and rules."""
    validator = BlueprintValidator(Path(args.schema) if args.schema else None)
    target = Path(args.target)

    targets: list[Path] = []
    if target.is_file():
        targets.append(target)
    elif target.is_dir():
        targets.extend(sorted(target.glob("**/blueprint.json")))
        if not targets:
            # Also check for any .json files in that directory
            targets.extend(sorted(target.glob("*.json")))
    else:
        print(f"ERROR: Target path does not exist: {target}", file=sys.stderr)
        return 1

    if not targets:
        print(f"No blueprint files found under {target}", file=sys.stderr)
        return 1

    total_failures = 0
    for file_path in targets:
        failures = validator.validate_file(file_path)
        if failures:
            total_failures += len(failures)
            print(f"FAIL: {file_path}", file=sys.stderr)
            for failure in failures:
                print(f"  - {failure}", file=sys.stderr)
        else:
            print(f"PASS: {file_path}")

    if total_failures > 0:
        print(f"\nTotal validation failures: {total_failures}", file=sys.stderr)
        return 1

    print(f"\nAll {len(targets)} blueprint(s) validated successfully.")
    return 0


def cmd_package(args: argparse.Namespace) -> int:
    """Validate and package a blueprint into an immutable bundle manifest."""
    validator = BlueprintValidator(Path(args.schema) if args.schema else None)
    blueprint_path = Path(args.target)

    if blueprint_path.is_dir():
        blueprint_path = blueprint_path / "blueprint.json"

    if not blueprint_path.is_file():
        print(f"ERROR: Blueprint file not found: {blueprint_path}", file=sys.stderr)
        return 1

    failures = validator.validate_file(blueprint_path)
    if failures:
        print(f"ERROR: Cannot package invalid blueprint {blueprint_path}:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1

    raw_bytes = blueprint_path.read_bytes()
    blueprint_data = json.loads(raw_bytes.decode("utf-8"))

    # Canonicalize JSON (sorted keys, compact separators) for stable digest
    canonical_bytes = json.dumps(blueprint_data, sort_keys=True, separators=(",", ":")).encode("utf-8")
    content_digest = hashlib.sha256(canonical_bytes).hexdigest()

    package_bundle = {
        "manifestVersion": "1.0.0",
        "packagedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "namespace": blueprint_data["namespace"],
        "blueprintId": blueprint_data["blueprintId"],
        "revision": blueprint_data["revision"],
        "sha256": content_digest,
        "blueprint": blueprint_data
    }

    bundle_json = json.dumps(package_bundle, indent=2) + "\n"

    if args.output:
        out_path = Path(args.output)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(bundle_json, encoding="utf-8")
        print(f"Blueprint packaged successfully: {out_path} (digest: {content_digest})")
    else:
        print(bundle_json)

    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="LaPluma Document Blueprint Onboarding and Validation CLI (INF-17)",
        prog="blueprint_cli.py"
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    # init
    p_init = subparsers.add_parser("init", help="Scaffold a new blueprint template")
    p_init.add_argument("--namespace", required=True, help="Namespace (e.g. uscis, institutional)")
    p_init.add_argument("--id", required=True, help="Blueprint ID (e.g. i-130, intake)")
    p_init.add_argument("--title", required=True, help="Display title")
    p_init.add_argument("--issuer", required=True, help="Issuing agency or organization")
    p_init.add_argument("--mode", choices=["FILLABLE_PDF", "STATIC_ASSISTED", "EXTERNAL_REFERENCE"], default="FILLABLE_PDF")
    p_init.add_argument("--artifact-type", choices=["OFFICIAL_PDF", "XFA", "FLAT", "EXTERNAL_LINK", "AUTHORED_TEMPLATE"], default="OFFICIAL_PDF")
    p_init.add_argument("--access-scope", choices=["SHARED_OFFICIAL", "INSTITUTION_PRIVATE"], default="SHARED_OFFICIAL")
    p_init.add_argument("--revision", type=int, default=1)
    p_init.add_argument("--edition-date", help="Official edition date (YYYY-MM-DD)")
    p_init.add_argument("--source-url", help="Official source download URL")
    p_init.add_argument("--source-sha256", help="SHA256 checksum of source document")
    p_init.add_argument("-o", "--output", default="blueprint.json", help="Output file path or directory")
    p_init.add_argument("-f", "--force", action="store_true", help="Overwrite existing output file")
    p_init.set_defaults(func=cmd_init)

    # validate
    p_val = subparsers.add_parser("validate", help="Validate blueprint file or directory")
    p_val.add_argument("target", help="Path to blueprint.json or directory containing blueprints")
    p_val.add_argument("--schema", help="Path to custom JSON schema file")
    p_val.set_defaults(func=cmd_validate)

    # package
    p_pkg = subparsers.add_parser("package", help="Validate and package blueprint into a bundle manifest")
    p_pkg.add_argument("target", help="Path to blueprint.json or directory containing it")
    p_pkg.add_argument("--schema", help="Path to custom JSON schema file")
    p_pkg.add_argument("-o", "--output", help="Output package file path (defaults to stdout)")
    p_pkg.set_defaults(func=cmd_package)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
