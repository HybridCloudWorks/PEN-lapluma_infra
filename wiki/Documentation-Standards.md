# Documentation standards

There must never be competing sources of truth. Content determines destination; a filename never
does.

## Approved destinations

| Destination | Holds | Holds nothing else |
|-------------|-------|--------------------|
| `README.md` (repository root only) | Repository purpose, overview, quick start, installation, configuration overview, repository conventions, navigation to documentation | No architecture, design, ADRs, roadmaps, plans, technical debt, engineering notes, blockers, TODOs, migration or remediation plans, work logs, status reports, runbooks, or troubleshooting guides |
| `CHANGELOG.md` | Completed feature additions, enhancements, fixes, released breaking changes, completed security fixes | No planned or future work, technical debt, open bugs, blockers, TODOs, design discussion, or work in progress |
| `REVIEW.md` | Only blockers a human decision, approval, credential, or access grant can clear | No bugs, refactoring, technical debt, enhancements, test work, documentation work, or general engineering tasks |
| `TODO.md` | Every actionable engineering item discovered anywhere in the repository | — |
| This wiki | Everything else: architecture, design, ADRs, roadmaps, plans, remediation plans, runbooks, troubleshooting, feature docs, development and migration guides, engineering notes, test strategies, operational procedures, support docs, knowledge transfer, research, design reviews, investigation results | — |

If content is not directly related to understanding, installing, configuring, onboarding to, or
navigating the repository, it does not belong in `README.md`. If the work is not complete, it does
not belong in `CHANGELOG.md`. If an engineer can resolve it independently, it belongs in `TODO.md`,
not `REVIEW.md`.

## Classification

Classify by content, not by filename.

1. Is this repository-purpose information? → `README.md`
2. Is this completed work? → `CHANGELOG.md`
3. Does this require a human decision or action that an engineer cannot take? → `REVIEW.md`
4. Is this actionable engineering work? → `TODO.md`
5. None of the above? → this wiki

## Required fields

### `REVIEW.md` items

Problem · Why it blocks progress · Required owner · Required action · Impact if unresolved ·
References · Recommended next step.

Every blocker needs a clearly identified owner and a clearly described action.

### `TODO.md` items

Title · Priority · Description · Dependencies · Recommended action · Status · Notes for future
engineers.

Items are ordered chronologically, in dependency order, and by implementation phase, so an engineer
can resume work without rediscovering findings.

## Dot-prefixed folders

Dot-prefixed folders — `.github/`, `.vscode/`, `.devcontainer/`, `.azure/`, `.claude/`, `.config/`,
and the like — are configuration locations, not documentation repositories. Avoid them where
possible, and never create documentation inside one.

Allowed content is tool, platform, repository, IDE, agent, and automation configuration only:
workflow definitions, Dependabot configuration, CODEOWNERS, linter configuration, dev-container
configuration, IDE settings, agent configuration, and repository automation settings.

The only acceptable markdown exceptions are files a platform or tool explicitly requires — for
example `.github/PULL_REQUEST_TEMPLATE.md`, `.github/ISSUE_TEMPLATE.md`, `.github/SECURITY.md`,
`.github/SUPPORT.md`, `.github/CODE_OF_CONDUCT.md`, and `.github/CONTRIBUTING.md`. Keep those
minimal, avoid duplication, and link here instead of restating content.

Any other markdown file found in a dot-prefixed folder must be classified, validated, migrated,
verified, and only then recommended for deletion.

## Non-root README files

Any `README.md` outside the repository root is documentation, not repository metadata. Extract its
useful content, move the long-form parts to this wiki, remove duplication, preserve context, verify
that nothing was lost and that cross-references still resolve, and only then recommend deleting it.
Record the migration in the consolidation report and add follow-up work to `TODO.md` if the cleanup
cannot be completed immediately.

## Workflow

Classify → validate → migrate → verify → recommend cleanup.

Validation happens **before** migration: verify statements against repository evidence, remove
obsolete information, check links and references, confirm implementation details, and remove
duplicates. Never migrate unverified content, and never recommend deleting an original before the
migration has been verified.

## End state

```
README.md    → repository purpose
CHANGELOG.md → completed features
REVIEW.md    → human-resolvable blockers
TODO.md      → engineering work queue
Wiki         → all remaining documentation
```
