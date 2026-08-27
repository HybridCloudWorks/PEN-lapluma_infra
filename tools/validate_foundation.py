#!/usr/bin/env python3
"""Deterministic, dependency-free validation for the placeholder-only foundation."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]


class Failures(list[str]):
    """Failures collected by one check.

    Each check owns its own list rather than appending to a module-level global, so a check can be
    run more than once, or on its own, without inheriting another's results.
    """

    def require(self, condition: bool, message: str) -> None:
        if not condition:
            self.append(message)

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


PROHIBITED_INPUTS = (
    "userid",
    "personid",
    "folderid",
    "caseid",
    "documentid",
    "eligibility",
    "facts",
)


def collect_parameter_names(paths: dict[str, Any]) -> tuple[set[str], list[str]]:
    """Return every declared parameter name, lowercased, plus any structural failures.

    OpenAPI permits `parameters` as a sibling of `get`/`post` on the path item, applying to every
    operation beneath it. Reading operations alone misses those entirely, so a prohibited input
    declared that way walks past the rule that exists to keep person, case, and eligibility
    identifiers out of the catalog API. A `$ref` parameter has no `name` at all; this gate rejects
    it rather than dereferencing, because a rule that cannot read a declaration must not pass it.
    """
    names: set[str] = set()
    failures: list[str] = []
    for path, path_item in paths.items():
        # Every shape below is checked before it is read. This rule exists to report a contract
        # violation, so malformed input has to reach CI as the ERROR line the log is scanned for,
        # not as a traceback from inside the rule.
        if not isinstance(path_item, dict):
            failures.append(f"catalog path item is not an object: {path}")
            continue

        declarations: list[Any] = []
        sources = [path_item.get("parameters")]
        for key, operation in path_item.items():
            if key != "parameters" and isinstance(operation, dict):
                sources.append(operation.get("parameters"))
        for source in sources:
            if source is None:
                continue
            if not isinstance(source, list):
                failures.append(f"catalog parameters must be declared as a list: {path}")
                continue
            declarations.extend(source)

        for declaration in declarations:
            if not isinstance(declaration, dict):
                failures.append(f"catalog parameter declaration is not an object: {path}")
                continue
            if "$ref" in declaration:
                failures.append(
                    f"catalog parameters must be declared inline so they can be read: {path}"
                )
                continue
            name = declaration.get("name")
            if isinstance(name, str):
                names.add(name.lower())
            else:
                failures.append(f"catalog parameter declares no name: {path}")
    return names, failures


def validate_openapi() -> Failures:
    failures = Failures()
    require = failures.require
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

    parameter_names, collection_failures = collect_parameter_names(paths)
    for failure in collection_failures:
        require(False, failure)
    for prohibited in PROHIBITED_INPUTS:
        require(
            prohibited not in parameter_names,
            f"catalog operation accepts prohibited input: {prohibited}",
        )
    for path, path_item in paths.items():
        for key, operation in path_item.items():
            if isinstance(operation, dict) and "requestBody" in operation:
                require(False, f"catalog operations accept no request body: {key.upper()} {path}")

    schemas = document.get("components", {}).get("schemas", {})
    require(bool(schemas), "catalog contract must declare component schemas")
    expected_enums = {
        "FormArtifactKind": ["OFFICIAL_PDF", "EXTERNAL_WORKFLOW", "PROPRIETARY_FORM", "AUTHORED_TEMPLATE"],
        "FormFillCapability": ["AUTOMATIC_FILL", "ASSISTED_PREPARATION", "REFERENCE_ONLY"],
        "FormActivationState": ["UNAVAILABLE", "CATALOG_ONLY", "ASSISTED", "PILOT"],
        "FormEncoding": ["ACROFORM", "XFA", "FLAT"],
    }
    for schema_name, expected in expected_enums.items():
        require(schemas.get(schema_name, {}).get("enum") == expected, f"{schema_name} enum drifted")
    require(
        schemas.get("FormEditionId", {}).get("required") == ["authority", "formID", "editionDate"],
        "edition identity must be authority-aware",
    )
    require(
        {"sourcePageURL", "artifactURL"}.issubset(
            schemas.get("FormSourceMetadata", {}).get("properties", {})
        ),
        "source URL keys must match the Swift contract",
    )
    require(
        "activationState" not in schemas.get("FormPackage", {}).get("properties", {}),
        "package activation must be derived from child forms, matching the Swift contract",
    )
    for schema_name, property_name in (
        ("FormSourceMetadata", "sourcePageURL"),
        ("FormSourceMetadata", "artifactURL"),
        ("FormPackage", "sourceURL"),
        ("FormPackage", "feeCitationURL"),
    ):
        require(
            schemas.get(schema_name, {}).get("properties", {}).get(property_name, {}).get("pattern")
            == "^https://",
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
    return failures


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


def validate_priority_and_modes() -> Failures:
    failures = Failures()
    require = failures.require
    source = (ROOT / "src/core-api/CatalogRepository.cs").read_text(encoding="utf-8")
    function_contract = (ROOT / "src/functions/acquisition_contract.py").read_text(encoding="utf-8")
    compatibility = json.loads(
        (ROOT / "contracts/catalog-package-compatibility.json").read_text(encoding="utf-8")
    )
    require(compatibility.get("contractVersion") == "lapluma-app-0.2", "package contract version drifted")
    expected_packages = {
        "FAMILY_I130": ["I-130", "I-130A"],
        "ADJUSTMENT_I485_I864": ["I-485", "I-864"],
        "NATURALIZATION_N400": ["N-400"],
        "EAD_I765": ["I-765"],
        "TRAVEL_I131": ["I-131"],
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
    # The catalog *listing* tracks the app's lapluma-app-0.2 snapshot (seven packages, enforced by
    # the app's ContractCompatibilityTests). The *acquisition scope* stays the four ratified
    # PILOT_PRIORITY_FORMS. These are different boundaries: a form the catalog lists is not thereby
    # a form the acquisition sweep may fetch, so the three catalog-only forms must appear in the
    # fixture and must never appear in the acquisition contract.
    for form_id in ("N-400", "I-765", "I-131"):
        require(form_id in source, f"core catalog fixture is missing {form_id}")
        require(
            form_id not in function_contract,
            f"non-priority form {form_id} leaked into the acquisition scope",
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
        set(classifications)
        == {"I-130", "I-130A", "I-485", "I-864", "N-400", "I-765", "I-131", "DS-11", "FAFSA"},
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
    return failures


# The adopted revision of the app-authored workflow contract. The mirror must stay byte-identical
# to the file the iOS client is generated from, so drift is a hash mismatch rather than a silent
# divergence; adopting a newer revision is a deliberate act that updates this constant in the same
# change. The cross-repository pin (the app ledger's LAPLUMA_CONTRACT_REVISION) is still format
# agreed only — see R-19.
WORKFORCE_WORKFLOW_SHA256 = "09b0bce5fc03d244cd77a6a40b415827823e0c095019198f031dc72a223a8d9a"


def validate_workflow_contract() -> Failures:
    """Text-level checks over the YAML workflow contracts.

    This validator is dependency-free and has no YAML parser, so these rules read the documents as
    text. They exist to keep the load-bearing lines visible in review — the placeholder server, the
    declared auth scheme, the anonymous relay surface — not to validate structure: a structural
    mistake surfaces when the contract generates a client.
    """
    failures = Failures()
    require = failures.require

    mirror = ROOT / "contracts/openapi/workforce-workflow.yaml"
    authored = ROOT / "contracts/openapi/documents-upload.yaml"
    missing = [path for path in (mirror, authored) if not path.exists()]
    for path in missing:
        require(False, f"workflow contract missing: {path.relative_to(ROOT)}")
    if missing:
        return failures

    mirror_bytes = mirror.read_bytes()
    require(
        hashlib.sha256(mirror_bytes).hexdigest() == WORKFORCE_WORKFLOW_SHA256,
        "workforce-workflow.yaml no longer matches the adopted contract revision; adopting a new "
        "revision means updating WORKFORCE_WORKFLOW_SHA256 in the same change, deliberately",
    )

    mirror_text = mirror_bytes.decode("utf-8")
    authored_text = authored.read_text(encoding="utf-8")
    for text, name in ((mirror_text, mirror.name), (authored_text, authored.name)):
        require(
            text.startswith("openapi: 3.1.0\n"),
            f"{name} must declare OpenAPI 3.1.0 on its first line",
        )
        require(
            'servers: [{url: "https://api.example.invalid/v1"}]' in text,
            f"{name} must keep the placeholder server URL until R-07 lands a hostname",
        )
        require(
            "bearerFormat: opaque-session" in text,
            f"{name} auth scheme drifted from the opaque session bearer the app expects",
        )
        require("Idempotency-Key" in text, f"{name} must require the Idempotency-Key header")
        require("version: 0.2.0" in text, f"{name} version drifted from 0.2.0")

    require("title: LaPluma Workflow API" in mirror_text, "workflow mirror title drifted")
    require(
        mirror_text.count("security: []") == 2,
        "the workflow mirror must carry exactly two anonymous operations "
        "(the relay challenge and unlock)",
    )
    require(
        "title: LaPluma Documents Upload API" in authored_text,
        "upload contract title drifted",
    )
    require(
        "security: []" not in authored_text,
        "the upload contract must not declare anonymous operations",
    )
    return failures


def validate_azure_interlock() -> Failures:
    failures = Failures()
    require = failures.require
    azure_yaml = (ROOT / "azure.yaml").read_text(encoding="utf-8")
    for service in ("core-api", "workflow-api", "processing-worker", "acquisition-functions"):
        require(f"  {service}:" in azure_yaml, f"azure.yaml missing {service}")

    parameters = json.loads((ROOT / "infra/main.parameters.json").read_text(encoding="utf-8"))
    interlock = parameters.get("parameters", {}).get("enableProvisioning", {}).get("value")
    require(interlock is False, "provisioning interlock must remain false")

    main_bicep = (ROOT / "infra/main.bicep").read_text(encoding="utf-8")
    require("param enableProvisioning bool = false" in main_bicep, "Bicep interlock default must be false")
    require("@allowed([\n  false\n])" in main_bicep, "Bicep interlock must reject a true override")
    require("subscription().tenantId" not in main_bicep, "tenant resolution belongs inside scoped modules only")

    data_bicep = (ROOT / "infra/modules/data.bicep").read_text(encoding="utf-8")
    security_bicep = (ROOT / "infra/modules/security.bicep").read_text(encoding="utf-8")
    require("for account in storageAccounts" not in data_bicep, "resource collections must be indexed in outputs")
    require("kv-lapluma-${suffix}" in security_bicep, "Key Vault name must stay within its 24-character limit")
    return failures


APP_SETTINGS_MARKER = "# --- Application settings ---"


def scan_binding_placeholders(root: Path) -> set[str]:
    """Every %NAME% the Functions host must resolve before it will start."""
    names: set[str] = set()
    for path in (root / "src/functions").rglob("*"):
        if path.is_file() and path.suffix in {".py", ".json"}:
            names |= set(re.findall(r"%([A-Z][A-Z0-9_]*)%", path.read_text(encoding="utf-8")))
    return names


def mentioned_in_source(root: Path, name: str) -> bool:
    """Whether a setting name appears in the services at all.

    Deliberately a literal search rather than a scan for `os.environ`: a value can be read through
    an indirection, and a rule that only recognises one access form reports a live setting as
    stale.
    """
    for path in (root / "src").rglob("*"):
        if path.is_file() and path.suffix in {".py", ".json"}:
            if name in path.read_text(encoding="utf-8"):
                return True
    return False


def validate_env_example() -> Failures:
    failures = Failures()
    require = failures.require
    parameters_text = (ROOT / "infra/main.parameters.json").read_text(encoding="utf-8")
    referenced = set(re.findall(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", parameters_text))

    path = ROOT / ".env.example"
    if not path.is_file():
        require(False, ".env.example must exist and list every substituted parameter variable")
        return failures

    declaration = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$")
    documented: dict[str, str] = {}
    app_settings: dict[str, str] = {}
    in_app_settings = False
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.strip()
        if stripped == APP_SETTINGS_MARKER:
            in_app_settings = True
            continue
        if not stripped or stripped.startswith("#"):
            continue
        match = declaration.match(stripped)
        if match is None:
            require(False, f".env.example line {number} is neither a comment nor a NAME= declaration")
            continue
        target = app_settings if in_app_settings else documented
        target[match.group(1)] = match.group(2).strip()

    # Application settings are checked against the services, not against the Bicep parameters.
    bound = scan_binding_placeholders(ROOT)
    undeclared = sorted(bound - app_settings.keys())
    require(
        not undeclared,
        f".env.example does not declare app settings the services bind: {', '.join(undeclared)}",
    )
    stale = sorted(name for name in app_settings if not mentioned_in_source(ROOT, name))
    require(not stale, f".env.example declares app settings nothing uses: {', '.join(stale)}")
    populated_settings = sorted(name for name, value in app_settings.items() if value)
    require(
        not populated_settings,
        f".env.example must carry no value: {', '.join(populated_settings)}",
    )

    missing = sorted(referenced - documented.keys())
    require(not missing, f".env.example is missing parameter variables: {', '.join(missing)}")
    unreferenced = sorted(documented.keys() - referenced)
    require(
        not unreferenced,
        f".env.example declares variables no parameter substitutes: {', '.join(unreferenced)}",
    )
    populated = sorted(name for name, value in documented.items() if value)
    require(not populated, f".env.example must carry no value: {', '.join(populated)}")
    return failures


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


def validate_no_sensitive_values() -> Failures:
    failures = Failures()
    require = failures.require
    # This module defines the patterns as literals, so it matches itself and is always skipped.
    for finding in scan_for_secrets(ROOT, frozenset({Path(__file__).resolve()})):
        require(False, finding)
    return failures


CODEQL_REFERENCE = re.compile(r"uses:\s*(github/codeql-action/[A-Za-z-]+)@([0-9a-f]{40})")
UNPINNED_ACTION = re.compile(r"uses:\s*([A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+)@(?!\s*[0-9a-f]{40}\b)(\S+)")


def validate_workflow_action_pins() -> Failures:
    """Third-party actions are pinned by commit SHA, and CodeQL's are pinned to one SHA.

    The sub-actions of github/codeql-action are one product released as a set. Bumping init without
    analyze produces `CodeQL job status was configuration error` rather than a version warning, and
    an upload-sarif left behind on the previous major is the same mismatch with no obvious symptom.
    Dependabot raises one pull request per sub-action, so this arrangement is what it proposes by
    default and would recur on every release.
    """
    failures = Failures()
    workflows = sorted((ROOT / ".github").rglob("*.yml"))

    codeql: dict[str, set[str]] = {}
    for path in workflows:
        text = path.read_text(encoding="utf-8")
        for action, sha in CODEQL_REFERENCE.findall(text):
            codeql.setdefault(sha, set()).add(action)
        for action, ref in UNPINNED_ACTION.findall(text):
            # Repository-local composite actions are read from the checked-out tree, not resolved
            # from a remote ref, so there is nothing to pin.
            if action.startswith("./"):
                continue
            failures.require(
                False,
                f"{path.relative_to(ROOT)} pins {action} to {ref!r}; use a full commit SHA",
            )

    failures.require(
        len(codeql) <= 1,
        "github/codeql-action sub-actions must share one commit SHA, found "
        + "; ".join(f"{sha[:12]} -> {', '.join(sorted(names))}" for sha, names in sorted(codeql.items())),
    )
    return failures


# Where the interpreter version is stated. These have to agree, and the set is explicit rather than
# a repository-wide scan so that prose discussing a past or proposed version does not fail the rule.
PYTHON_VERSION_SOURCES = (
    ("src/document-processing/Dockerfile", re.compile(r"FROM python:(\d+\.\d+)-slim")),
    (".github/workflows/foundation-validation.yml", re.compile(r"python-version:\s*'(\d+\.\d+)'")),
    # The function host. Until the hosting layer existed there was nothing here to check, and the
    # runtime could have drifted from the image and the requirements that constrain it.
    ("infra/modules/compute.bicep", re.compile(r"param functionsPythonVersion string = '(\d+\.\d+)'")),
    ("infra/main.bicep", re.compile(r"param functionsPythonVersion string = '(\d+\.\d+)'")),
    ("src/document-processing/worker.py", re.compile(r"Python (\d+\.\d+)")),
    ("README.md", re.compile(r"Python (\d+\.\d+)")),
    ("wiki/Architecture-Overview.md", re.compile(r"Python (\d+\.\d+)")),
    ("wiki/Azure-Deployment-Plan.md", re.compile(r"Python (\d+\.\d+)")),
)


def validate_python_version_agreement() -> Failures:
    """One interpreter version, stated the same way everywhere.

    The version is not a per-component choice. `src/functions/requirements.txt` is bound to it —
    azure-functions 2.x requires >=3.13 and the 1.x line caps at <3.13 — so a bump applied to one
    file is wrong wherever it is applied alone. The image, CI, and the documentation drifting apart
    is the failure this prevents: CI would keep testing on one interpreter while the container
    shipped another, and nothing would say so.
    """
    failures = Failures()
    found: dict[str, list[str]] = {}
    for relative, pattern in PYTHON_VERSION_SOURCES:
        path = ROOT / relative
        if not path.is_file():
            failures.require(False, f"python version source is missing: {relative}")
            continue
        versions = pattern.findall(path.read_text(encoding="utf-8"))
        if not versions:
            failures.require(False, f"{relative} states no Python version; the rule cannot check it")
            continue
        for version in versions:
            found.setdefault(version, []).append(relative)

    failures.require(
        len(found) <= 1,
        "the Python version must be the same everywhere, found "
        + "; ".join(f"{v} in {', '.join(sorted(set(f)))}" for v, f in sorted(found.items())),
    )
    return failures


# A resource that turns off public access is unreachable until a private endpoint replaces it. This
# maps each such resource to the module output its endpoint is wired from. Both directions are
# checked: a resource missing from this map fails, and a map entry that main.bicep does not use in
# its privatelink targets fails. Adding a locked-down service without an endpoint therefore cannot
# pass, which is the failure the foundation shipped with.
PRIVATE_ACCESS_EXPECTATIONS = {
    ("data.bicep", "sqlServer"): "sqlServerId",
    ("data.bicep", "cosmos"): "cosmosId",
    ("data.bicep", "storageAccounts"): "storageAccountIds",
    ("messaging.bicep", "serviceBus"): "serviceBusId",
    ("security.bicep", "keyVault"): "keyVaultId",
    ("security.bicep", "managedHsm"): "managedHsmId",
    ("compute.bicep", "registry"): "registryId",
    ("compute.bicep", "functionsStorage"): "functionsStorageId",
    # Azure Monitor is reached through one endpoint on the private link scope rather than one per
    # component, so both the workspace and the component map to the scope.
    ("observability.bicep", "workspace"): "privateLinkScopeId",
    ("observability.bicep", "applicationInsights"): "privateLinkScopeId",
    # No public access to disable: the function app is reached through its own inbound restriction.
    ("compute.bicep", "functionApp"): None,
}

RESOURCE_DECLARATION = re.compile(r"^resource (\w+) '([^']+)'", re.MULTILINE)
PUBLIC_ACCESS_DISABLED = re.compile(
    r"publicNetworkAccess(?:ForIngestion|ForQuery)?:\s*(?:'Disabled'|monitorPublicNetworkAccess)"
)


def resources_disabling_public_access(module: Path) -> set[str]:
    """Symbolic names in one module whose declaration turns public network access off."""
    text = module.read_text(encoding="utf-8")
    declarations = list(RESOURCE_DECLARATION.finditer(text))
    disabled: set[str] = set()
    for index, match in enumerate(declarations):
        end = declarations[index + 1].start() if index + 1 < len(declarations) else len(text)
        if PUBLIC_ACCESS_DISABLED.search(text[match.start():end]):
            disabled.add(match.group(1))
    return disabled


def validate_private_endpoint_coverage() -> Failures:
    """Every service that disables public access has a private endpoint wired to it."""
    failures = Failures()
    main = (ROOT / "infra/main.bicep").read_text(encoding="utf-8")

    # Only the privatelink module's target list counts. A reference anywhere else in main.bicep is
    # not an endpoint, and matching on it would let this rule pass on an unrelated mention.
    start = main.find("module privatelink ")
    end = main.find("\nmodule ", start + 1) if start != -1 else -1
    targets = main[start:end if end != -1 else len(main)] if start != -1 else ""
    failures.require(bool(targets), "infra/main.bicep declares no privatelink module")

    for module in sorted((ROOT / "infra/modules").glob("*.bicep")):
        for symbol in sorted(resources_disabling_public_access(module)):
            key = (module.name, symbol)
            if key not in PRIVATE_ACCESS_EXPECTATIONS:
                failures.require(
                    False,
                    f"{module.name} resource '{symbol}' disables public network access but no "
                    "private endpoint is recorded for it in PRIVATE_ACCESS_EXPECTATIONS",
                )
                continue
            output = PRIVATE_ACCESS_EXPECTATIONS[key]
            if output is None:
                continue
            failures.require(
                f".outputs.{output}" in targets,
                f"{module.name} resource '{symbol}' disables public network access, but "
                f"main.bicep's privatelink targets never use '{output}'",
            )
    return failures


def validate_ai_zone_has_no_data_plane_role() -> Failures:
    """The AI zone holds no authoritative data-plane role, and that is checked rather than asserted.

    security.bicep creates an AI identity and the design gives it nothing. A role assignment added
    for it later would be a trust-zone change disguised as a convenience.
    """
    failures = Failures()
    rbac = ROOT / "infra/modules/rbac.bicep"
    if not rbac.is_file():
        failures.require(False, "infra/modules/rbac.bicep is missing")
        return failures

    text = rbac.read_text(encoding="utf-8")
    for forbidden in ("aiPrincipalId", "aiIdentity"):
        # Comments explain the absence, so only a real reference counts.
        code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("//"))
        failures.require(
            forbidden not in code,
            f"rbac.bicep references '{forbidden}': the AI zone must hold no data-plane role",
        )

    failures.require(
        "sqlRoleAssignments" not in text.split("Processing zone")[-1].split("Functions zone")[0],
        "the processing zone must never receive a Cosmos or SQL role",
    )
    return failures


# Resource types that emit diagnostics and therefore must route them to the workspace. A resource
# added without a diagnostic setting is one whose audit trail simply does not exist, and nothing
# about the deployment says so — the failure is silence.
DIAGNOSABLE_TYPES = frozenset({
    "Microsoft.Sql/servers/databases",
    "Microsoft.DocumentDB/databaseAccounts",
    "Microsoft.Storage/storageAccounts",
    "Microsoft.Storage/storageAccounts/blobServices",
    "Microsoft.ServiceBus/namespaces",
    "Microsoft.KeyVault/vaults",
    "Microsoft.KeyVault/managedHSMs",
    "Microsoft.Network/networkSecurityGroups",
    "Microsoft.Network/virtualNetworks",
    "Microsoft.App/managedEnvironments",
    "Microsoft.ContainerRegistry/registries",
    "Microsoft.Web/sites",
})

DECLARATION = re.compile(r"^resource (\w+) '([^'@]+)@[^']+'(\s+existing)?\s*=", re.MULTILINE)
DIAGNOSTIC_SCOPE = re.compile(r"^\s*scope: (\w+)(?:\[[^\]]*\])?\s*$", re.MULTILINE)


def validate_diagnostic_coverage() -> Failures:
    """Every resource that can emit diagnostics routes them to the workspace."""
    failures = Failures()
    for module in sorted((ROOT / "infra/modules").glob("*.bicep")):
        text = module.read_text(encoding="utf-8")

        # Only scopes inside a diagnosticSettings declaration count. A `scope:` elsewhere — a role
        # assignment, for instance — is not a diagnostic setting and must not satisfy this.
        scoped: set[str] = set()
        for block in text.split("resource ")[1:]:
            if block.lstrip().startswith("_") or "'Microsoft.Insights/diagnosticSettings@" not in block.split("\n")[0]:
                continue
            body = block.split("\n}", 1)[0]
            scoped.update(DIAGNOSTIC_SCOPE.findall(body))

        for symbol, resource_type, is_existing in DECLARATION.findall(text):
            if is_existing or resource_type not in DIAGNOSABLE_TYPES:
                continue
            failures.require(
                symbol in scoped,
                f"{module.name} declares {resource_type} '{symbol}' with no diagnostic setting "
                "routing it to the workspace",
            )
    return failures


# Subnet delegation is not a free choice: each Functions hosting SKU integrates through a specific
# delegated service, and the two must agree. This map is recorded from the Azure Component Research
# Record rather than derived here; re-verify it against current Azure guidance before adding a SKU.
#
# The reason this is a rule and not a comment: a delegation cannot be changed while a resource
# occupies the subnet, so a mismatch is not caught at deployment and then fixed — it is caught at
# deployment and then requires rebuilding the VNet. The two declarations live in different files,
# which is exactly the shape of change where one gets updated and the other does not.
FUNCTIONS_SKU_DELEGATIONS = {
    "FC1": "Microsoft.App/environments",       # Flex Consumption
    "EP1": "Microsoft.Web/serverFarms",        # Elastic Premium
    "EP2": "Microsoft.Web/serverFarms",
    "EP3": "Microsoft.Web/serverFarms",
}

FUNCTIONS_PLAN_SKU = re.compile(
    r"resource functionsPlan 'Microsoft\.Web/serverfarms@[^']+'.*?sku:\s*\{\s*name:\s*'([^']+)'",
    re.DOTALL,
)
FUNCTIONS_SUBNET_DELEGATION = re.compile(
    r"name:\s*'snet-functions'.*?serviceName:\s*'([^']+)'",
    re.DOTALL,
)


def validate_functions_subnet_delegation() -> Failures:
    """The Functions subnet delegation matches the hosting SKU the plan declares."""
    failures = Failures()
    compute = ROOT / "infra/modules/compute.bicep"
    network = ROOT / "infra/modules/network.bicep"

    sku_match = FUNCTIONS_PLAN_SKU.search(compute.read_text(encoding="utf-8"))
    delegation_match = FUNCTIONS_SUBNET_DELEGATION.search(network.read_text(encoding="utf-8"))

    # Each side is reported separately, and a missing side is a failure rather than a skip. A rule
    # that quietly passes when it cannot find what it checks is the vacuous-pass failure this
    # repository has already been bitten by twice.
    failures.require(
        sku_match is not None,
        "compute.bicep declares no functionsPlan SKU; the delegation rule cannot check it",
    )
    failures.require(
        delegation_match is not None,
        "network.bicep declares no snet-functions delegation; the delegation rule cannot check it",
    )
    if sku_match is None or delegation_match is None:
        return failures

    sku = sku_match.group(1)
    delegation = delegation_match.group(1)
    expected = FUNCTIONS_SKU_DELEGATIONS.get(sku)

    if expected is None:
        failures.require(
            False,
            f"functionsPlan uses SKU '{sku}', which has no recorded subnet delegation; add it to "
            "FUNCTIONS_SKU_DELEGATIONS after verifying the requirement against Azure guidance",
        )
        return failures

    failures.require(
        delegation == expected,
        f"functionsPlan uses SKU '{sku}', which integrates through '{expected}', but "
        f"snet-functions is delegated to '{delegation}'. A delegation cannot be changed while the "
        "subnet is occupied, so this must be right before the first provisioning run",
    )
    return failures


# The ratified retention ordering rule, as a check rather than a paragraph. The contract itself is
# on the Pilot Policy and Compliance Gates wiki page.
#
# Every window that extends the life of case content must be STRICTLY shorter than the erasure SLA.
# A soft-deleted blob is still recoverable, which means it is still retained: if the recovery window
# reaches the SLA, the deletion receipt the data-flow design promises is false at the moment it is
# issued. Equal is not good enough — the two clocks start at different moments, so equality already
# means content outlives the promise.
#
# Two classes are exempt, and the reason is the same for both: they hold no case content. Audit
# metadata is content-free and pseudonymized on erasure, so it is the evidence that erasure happened
# rather than a surviving copy of what was erased. Key material is not case content either, and a
# long recovery window there costs nothing in privacy terms while buying a great deal in
# recoverability.
CONTENT_BEARING_WINDOWS = ("blobSoftDeleteDays", "containerSoftDeleteDays", "blobVersionDays")
RETENTION_DEFAULTS = re.compile(
    r"param retention RetentionBaseline = \{(.*?)\n\}",
    re.DOTALL,
)


def validate_retention_ordering() -> Failures:
    """No content-bearing retention window reaches the erasure SLA."""
    failures = Failures()
    text = (ROOT / "infra/main.bicep").read_text(encoding="utf-8")

    block = RETENTION_DEFAULTS.search(text)
    failures.require(
        block is not None,
        "main.bicep declares no retention defaults; the ordering rule cannot check them",
    )
    if block is None:
        return failures

    values = dict(re.findall(r"(\w+):\s*'(\d+)'", block.group(1)))
    sla = values.get("erasureSlaDays")
    failures.require(
        sla is not None,
        "the retention baseline declares no erasureSlaDays; the ordering rule has no ceiling to "
        "check against",
    )
    if sla is None:
        return failures

    for window in CONTENT_BEARING_WINDOWS:
        value = values.get(window)
        if value is None:
            failures.require(False, f"the retention baseline declares no {window}")
            continue
        failures.require(
            int(value) < int(sla),
            f"{window} is {value} days against a {sla}-day erasure SLA. A window that reaches the "
            "SLA keeps case content alive past the point the deletion receipt says it is gone. "
            "Either shorten the window or raise the SLA and tell the privacy owner, because the "
            "participant notice states the same number",
        )
    return failures


SUBSCRIPTION_DECLARATION = re.compile(
    r"^resource (\w+) 'Microsoft\.ServiceBus/namespaces/topics/subscriptions@",
    re.MULTILINE,
)


def validate_subscriptions_dead_letter() -> Failures:
    """Every Service Bus subscription dead-letters on message expiration.

    `deadLetteringOnMessageExpiration` belongs to the subscription, not to the topic — Bicep's
    `SBTopicProperties` rejects it — so the `domain-events` topic cannot set it once on behalf of
    everything beneath it. Each subscription has to set it individually, and a subscription that
    forgets discards expired messages with no trace.

    This rule matches nothing today, because `domain-events` has no subscriber yet. That is
    deliberate rather than an oversight: the failure it guards against arrives with the first
    subscription somebody adds, which is precisely the moment nobody is thinking about a fourteen-day
    TTL. A rule written then would have to be remembered; a rule written now cannot be forgotten.

    Its vacuous pass is therefore expected and is recorded here so a future reader does not mistake
    silence for coverage — the mutation that proves it works is adding a subscription without the
    flag, not removing one.
    """
    failures = Failures()
    module = ROOT / "infra/modules/messaging.bicep"
    text = module.read_text(encoding="utf-8")

    for block in text.split("resource ")[1:]:
        header = block.split("\n", 1)[0]
        if "'Microsoft.ServiceBus/namespaces/topics/subscriptions@" not in header:
            continue
        symbol = header.split(" ", 1)[0]
        body = block.split("\n}", 1)[0]
        failures.require(
            "deadLetteringOnMessageExpiration: true" in body,
            f"messaging.bicep declares subscription '{symbol}' without "
            "deadLetteringOnMessageExpiration: true; an expired message would be discarded with no "
            "trace. The property belongs to the subscription, not the topic, so the topic cannot "
            "set it on your behalf",
        )
    return failures


def main() -> int:
    failures = [
        *validate_openapi(),
        *validate_priority_and_modes(),
        *validate_workflow_contract(),
        *validate_azure_interlock(),
        *validate_env_example(),
        *validate_workflow_action_pins(),
        *validate_python_version_agreement(),
        *validate_private_endpoint_coverage(),
        *validate_ai_zone_has_no_data_plane_role(),
        *validate_diagnostic_coverage(),
        *validate_functions_subnet_delegation(),
        *validate_subscriptions_dead_letter(),
        *validate_retention_ordering(),
        *validate_no_sensitive_values(),
    ]
    if failures:
        for failure in failures:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 1
    print("Foundation validation passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
