#!/usr/bin/env python3
"""Deterministic, dependency-free validation for the placeholder-only foundation."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FAILURES: list[str] = []


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
    require("N-400" not in source and "I-765" not in source, "older priority forms leaked into Alpha 0.2 fixture")
    require("FAMILY_I130" in source and '"I-130A"' in source, "I-130 package must match the app contract")
    require(
        "ADJUSTMENT_I485_I864" in source and '"I-864"' in source,
        "I-485 package must match the app contract",
    )
    require("FormArtifactKind.ExternalWorkflow" in source, "FAFSA must remain an external workflow")
    require("FormFillCapability.ReferenceOnly" in source, "FAFSA must remain reference-only")
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


def validate_no_sensitive_values() -> None:
    excluded = {ROOT / ".git"}
    secret_patterns = {
        "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
        "Azure storage connection string": re.compile(r"DefaultEndpointsProtocol=https;AccountName="),
        "JWT-like token": re.compile(r"\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\b"),
        "concrete subscription assignment": re.compile(r"AZURE_SUBSCRIPTION_ID\s*[:=]\s*[0-9a-fA-F-]{36}"),
        "concrete tenant assignment": re.compile(r"AZURE_TENANT_ID\s*[:=]\s*[0-9a-fA-F-]{36}"),
    }
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(parent in excluded for parent in path.parents):
            continue
        if path == Path(__file__).resolve():
            continue
        if path.suffix.lower() in {".png", ".jpg", ".jpeg", ".gif", ".zip"}:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for label, pattern in secret_patterns.items():
            require(pattern.search(text) is None, f"{label} found in {path.relative_to(ROOT)}")


def main() -> int:
    validate_openapi()
    validate_priority_and_modes()
    validate_azure_interlock()
    validate_no_sensitive_values()
    if FAILURES:
        for failure in FAILURES:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 1
    print("Foundation validation passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
