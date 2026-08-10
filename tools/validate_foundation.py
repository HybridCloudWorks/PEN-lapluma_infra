#!/usr/bin/env python3
"""Deterministic, dependency-free validation for the placeholder-only foundation."""

from __future__ import annotations

import json
import re
import sys
from collections import Counter
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FAILURES: list[str] = []

# Generated output, not source. These mirror the directories .gitignore already excludes.
SKIPPED_DIRECTORIES = frozenset({".git", "__pycache__", "bin", "obj", ".venv", "node_modules"})
SKIPPED_SUFFIXES = frozenset({".png", ".jpg", ".jpeg", ".gif", ".zip"})
SECRET_PATTERNS = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    # Connection strings are unordered key/value pairs. Matching one fixed ordering missed every
    # other spelling of the same credential, so match a line carrying both parts instead.
    "Azure storage connection string": re.compile(
        r"^(?=[^\n]*AccountName=)(?=[^\n]*AccountKey=)[^\n]*$", re.MULTILINE
    ),
    # A bare account key, with no connection string around it. `allowSharedKeyAccess: false` is set
    # on every storage account, so one appearing here is an accident worth catching on its own.
    "Azure storage account key": re.compile(r"AccountKey=[A-Za-z0-9+/]{80,}={0,2}"),
    # A shared-access signature carries its own authority. The design permits managed identity only.
    "shared access signature": re.compile(r"[?&]sig=[A-Za-z0-9%+/]{20,}"),
    "JWT-like token": re.compile(r"\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\b"),
    "concrete subscription assignment": re.compile(r"AZURE_SUBSCRIPTION_ID\s*[:=]\s*[0-9a-fA-F-]{36}"),
    "concrete tenant assignment": re.compile(r"AZURE_TENANT_ID\s*[:=]\s*[0-9a-fA-F-]{36}"),
}


def require(condition: bool, message: str) -> None:
    if not condition:
        FAILURES.append(message)


def validate_openapi() -> None:
    path = ROOT / "contracts/catalog.openapi.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    require(document.get("openapi") == "3.1.0", "catalog contract must use OpenAPI 3.1.0")

    paths = document.get("paths", {})
    expected_paths = {
        "/health",
        "/ready",
        "/v1/catalog/categories",
        "/v1/catalog/packages",
        "/v1/catalog/packages/{packageCode}",
        "/v1/catalog/authorities/{authority}/forms/{formId}/editions/{editionDate}/schemas/{schemaVersion}",
    }
    require(set(paths) == expected_paths, "catalog contract path set drifted")

    parameter_names = {
        parameter["name"].lower()
        for path_item in paths.values()
        for operation in path_item.values()
        if isinstance(operation, dict)
        for parameter in operation.get("parameters", [])
    }
    for prohibited in ("userid", "personid", "folderid", "caseid", "documentid", "eligibility", "facts"):
        require(prohibited not in parameter_names, f"catalog operation accepts prohibited input: {prohibited}")

    schemas = document["components"]["schemas"]
    expected_enums = {
        "FormArtifactKind": ["OFFICIAL_PDF", "EXTERNAL_WORKFLOW", "PROPRIETARY_FORM", "AUTHORED_TEMPLATE"],
        "FormFillCapability": ["AUTOMATIC_FILL", "ASSISTED_PREPARATION", "REFERENCE_ONLY"],
        "FormActivationState": ["UNAVAILABLE", "CATALOG_ONLY", "ASSISTED", "PILOT"],
        "FormEncoding": ["ACROFORM", "XFA", "FLAT"],
    }
    for schema_name, expected in expected_enums.items():
        require(schemas[schema_name].get("enum") == expected, f"{schema_name} enum drifted")
    require(
        schemas["FormEditionId"].get("required") == ["authority", "formID", "editionDate"],
        "edition identity must be authority-aware",
    )
    require(
        {"sourcePageURL", "artifactURL"}.issubset(schemas["FormSourceMetadata"]["properties"]),
        "source URL keys must match the Swift contract",
    )
    require(
        "activationState" not in schemas["FormPackage"].get("properties", {}),
        "package activation must be derived from child forms, matching the Swift contract",
    )
    for schema_name, property_name in (
        ("FormSourceMetadata", "sourcePageURL"),
        ("FormSourceMetadata", "artifactURL"),
        ("FormPackage", "sourceURL"),
        ("FormPackage", "feeCitationURL"),
    ):
        require(
            schemas[schema_name]["properties"][property_name].get("pattern") == "^https://",
            f"{schema_name}.{property_name} must require HTTPS",
        )

    required_models = {
        "CatalogCategory",
        "CatalogSubcategory",
        "FormPackage",
        "CatalogForm",
        "FormEditionId",
        "FormSourceMetadata",
        "ExtractedFormSchema",
        "ExtractedFieldManifest",
    }
    require(required_models.issubset(schemas), "catalog model set is incomplete")


FORM_DECLARATION = re.compile(
    r'Form\(\s*"(?P<id>[^"]+)"\s*,\s*"[^"]*"\s*,\s*'
    r"FormArtifactKind\.(?P<kind>\w+)\s*,\s*"
    r"FormFillCapability\.(?P<capability>\w+)\s*,\s*"
    r"FormActivationState\.(?P<state>\w+)\s*\)"
)


def parse_form_declarations(source: str) -> list[tuple[str, tuple[str, str, str]]]:
    """Every form declaration in the catalog fixture, in file order, duplicates included.

    Each entry is (form number, (artifact kind, fill capability, activation state)). An empty
    result means the fixture no longer matches the expected call shape, which callers must treat
    as a failure rather than as "no form violates the rules".
    """
    return [
        (match.group("id"), (match.group("kind"), match.group("capability"), match.group("state")))
        for match in FORM_DECLARATION.finditer(source)
    ]


def parse_form_classifications(source: str) -> dict[str, tuple[str, str, str]]:
    """Map each form number to its classification triple.

    Collapses duplicate declarations — the last one wins. Callers that must detect a form
    declared twice have to read `parse_form_declarations` instead.
    """
    return dict(parse_form_declarations(source))


def validate_priority_and_modes() -> None:
    source = (ROOT / "src/core-api/CatalogRepository.cs").read_text(encoding="utf-8")
    function_contract = (ROOT / "src/functions/acquisition_contract.py").read_text(encoding="utf-8")
    compatibility = json.loads(
        (ROOT / "contracts/catalog-package-compatibility.json").read_text(encoding="utf-8")
    )
    require(compatibility.get("contractVersion") == "lapluma-app-0.2", "package contract version drifted")
    expected_packages = {
        "FAMILY_I130": ["I-130", "I-130A"],
        "ADJUSTMENT_I485_I864": ["I-485", "I-864"],
        "PASSPORT_DS11": ["DS-11"],
        "FINANCIAL_AID_FAFSA": ["FAFSA"],
    }
    require(
        {item["packageCode"]: item["formNumbers"] for item in compatibility.get("packages", [])}
        == expected_packages,
        "package compatibility fixture drifted",
    )
    for form_id in ("I-130", "I-485", "DS-11", "FAFSA"):
        require(form_id in source, f"core catalog fixture is missing {form_id}")
        require(form_id in function_contract, f"acquisition contract is missing {form_id}")
    for retired in ("N-400", "I-765"):
        require(retired not in source, f"older priority form {retired} leaked into the Alpha 0.2 fixture")
        require(
            retired not in function_contract,
            f"older priority form {retired} leaked into the acquisition scope",
        )
    require("FAMILY_I130" in source and '"I-130A"' in source, "I-130 package must match the app contract")
    require(
        "ADJUSTMENT_I485_I864" in source and '"I-864"' in source,
        "I-485 package must match the app contract",
    )

    # Bind each classification to the form it is declared on. Asserting that a literal appears
    # somewhere in the file passes just as happily when the classifications are swapped between
    # forms, which would silently make FAFSA an automatically fillable official PDF.
    declarations = parse_form_declarations(source)
    # Detect duplicates before collapsing to a dict: two declarations of one form would otherwise
    # leave only the last, hiding whatever the first one said.
    counts = Counter(form_id for form_id, _ in declarations)
    duplicated = sorted(form_id for form_id, count in counts.items() if count > 1)
    require(not duplicated, f"catalog fixture declares a form more than once: {duplicated}")
    classifications = dict(declarations)
    require(
        set(classifications) == {"I-130", "I-130A", "I-485", "I-864", "DS-11", "FAFSA"},
        f"catalog fixture form set drifted: {sorted(classifications)}",
    )
    require(
        classifications.get("FAFSA")
        == ("ExternalWorkflow", "ReferenceOnly", "Unavailable"),
        f"FAFSA must remain an external workflow, reference-only, and unactivated; "
        f"found {classifications.get('FAFSA')}",
    )
    for form_id, (kind, capability, _) in classifications.items():
        if form_id == "FAFSA":
            continue
        require(
            (kind, capability) == ("OfficialPdf", "AutomaticFill"),
            f"{form_id} classification drifted: {kind}, {capability}",
        )
    activated = sorted(
        form_id for form_id, (_, _, state) in classifications.items() if state == "Pilot"
    )
    require(not activated, f"placeholder catalog must not activate a pilot edition: {activated}")
    model_source = (ROOT / "src/core-api/CatalogModels.cs").read_text(encoding="utf-8")
    require("record FormEditionId(" in model_source and "string Authority" in model_source, "edition identity must include authority")
    require("FormActivationState.Pilot" not in source, "placeholder catalog must not activate a pilot edition")


def validate_azure_interlock() -> None:
    azure_yaml = (ROOT / "azure.yaml").read_text(encoding="utf-8")
    for service in ("core-api", "processing-worker", "acquisition-functions"):
        require(f"  {service}:" in azure_yaml, f"azure.yaml missing {service}")

    parameters = json.loads((ROOT / "infra/main.parameters.json").read_text(encoding="utf-8"))
    interlock = parameters["parameters"]["enableProvisioning"]["value"]
    require(interlock is False, "provisioning interlock must remain false")

    main_bicep = (ROOT / "infra/main.bicep").read_text(encoding="utf-8")
    require("param enableProvisioning bool = false" in main_bicep, "Bicep interlock default must be false")
    require("@allowed([\n  false\n])" in main_bicep, "Bicep interlock must reject a true override")
    require("subscription().tenantId" not in main_bicep, "tenant resolution belongs inside scoped modules only")

    data_bicep = (ROOT / "infra/modules/data.bicep").read_text(encoding="utf-8")
    security_bicep = (ROOT / "infra/modules/security.bicep").read_text(encoding="utf-8")
    require("for account in storageAccounts" not in data_bicep, "resource collections must be indexed in outputs")
    require("kv-lapluma-${suffix}" in security_bicep, "Key Vault name must stay within its 24-character limit")


def validate_env_example() -> None:
    parameters_text = (ROOT / "infra/main.parameters.json").read_text(encoding="utf-8")
    referenced = set(re.findall(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", parameters_text))

    path = ROOT / ".env.example"
    if not path.is_file():
        require(False, ".env.example must exist and list every substituted parameter variable")
        return

    declaration = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$")
    documented: dict[str, str] = {}
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        match = declaration.match(stripped)
        if match is None:
            require(False, f".env.example line {number} is neither a comment nor a NAME= declaration")
            continue
        documented[match.group(1)] = match.group(2).strip()

    missing = sorted(referenced - documented.keys())
    require(not missing, f".env.example is missing parameter variables: {', '.join(missing)}")
    unreferenced = sorted(documented.keys() - referenced)
    require(
        not unreferenced,
        f".env.example declares variables no parameter substitutes: {', '.join(unreferenced)}",
    )
    populated = sorted(name for name, value in documented.items() if value)
    require(not populated, f".env.example must carry no value: {', '.join(populated)}")


def scan_for_secrets(root: Path, ignored_files: frozenset[Path] = frozenset()) -> list[str]:
    """Return one finding per secret pattern matched in a file under root.

    Tracking is not consulted: every file under root is read whether or not git knows about it,
    because an untracked `.env` holding a real credential is exactly what this should catch.
    Generated directories are skipped so the scan covers source rather than build output —
    compiled Python bytecode in particular embeds this module's own pattern literals, which
    would otherwise be reported as a leaked connection string.
    """
    findings: list[str] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        if any(part in SKIPPED_DIRECTORIES for part in path.relative_to(root).parts[:-1]):
            continue
        if path.resolve() in ignored_files:
            continue
        if path.suffix.lower() in SKIPPED_SUFFIXES:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for label, pattern in SECRET_PATTERNS.items():
            if pattern.search(text) is not None:
                findings.append(f"{label} found in {path.relative_to(root)}")
    return findings


def validate_no_sensitive_values() -> None:
    # This module defines the patterns as literals, so it matches itself and is always skipped.
    for finding in scan_for_secrets(ROOT, frozenset({Path(__file__).resolve()})):
        require(False, finding)


def main() -> int:
    validate_openapi()
    validate_priority_and_modes()
    validate_azure_interlock()
    validate_env_example()
    validate_no_sensitive_values()
    if FAILURES:
        for failure in FAILURES:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 1
    print("Foundation validation passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
