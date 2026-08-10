<!--
Keep this short. Rationale that outlives the pull request belongs in CHANGELOG.md or the wiki.
-->

## What changed and why

<!-- One or two paragraphs. Link the TODO.md item or REVIEW.md ID this closes, if any. -->

## Verification

<!--
What you ran, and what it proved. "Tests pass" is not verification on its own — say what would have
failed had the change been wrong.
-->

## Invariant check

Confirm each, or say which does not apply and why:

- [ ] No credential, key, token, connection string, document content, or applicant identifier is
      added, including in test fixtures, expected-output examples, and comments.
- [ ] Telemetry added here is content-free: correlation identifiers only, never a path, query
      string, route value, or document identifier.
- [ ] The processing zone gains no route to SQL or Cosmos, and the AI zone gains no authoritative
      write.
- [ ] Automated components still propose rather than activate, approve, sign, or file.
- [ ] `enableProvisioning` remains restricted to `false`.
- [ ] Any new dependency, base image, or action is pinned by version, digest, or commit SHA.
