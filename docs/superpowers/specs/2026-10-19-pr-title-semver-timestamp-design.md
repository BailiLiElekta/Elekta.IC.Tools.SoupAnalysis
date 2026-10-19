# PR Title Semver and Timestamp

## Goal

Make each newly created Azure Repos pull request identify the configured release, the submission time, and the patch semver impact in its title.

## Title format

New PRs use this exact title shape:

```text
SOUP update for {analysisFor} {timestamp} + semver: patch
```

- `{analysisFor}` comes from the loaded platform configuration's `AnalysisFor` value.
- `{timestamp}` is the UTC time when the PR creation request is submitted, formatted as `yyyyMMddHHmmss` using invariant digits.
- ` + semver: patch` is fixed and must remain the final text in the title.

## Behavior

- Validate that `AnalysisFor` is not null, empty, or whitespace before any remote update begins. If it is missing, stop with an explicit configuration error; do not substitute a placeholder.
- Apply this title only when creating a new PR. Keep the PR description unchanged and do not modify an existing PR returned during reconciliation.
- Preserve existing PR reconciliation and duplicate-creation protections.

## Verification

- Test the serialized Azure PR creation request to verify the configured release is included, the timestamp is a valid 14-digit UTC timestamp, and the semver marker is exactly at the end.
- Test that a missing `AnalysisFor` prevents execution before remote update side effects.
- Run the smallest relevant test set and the applicable build/type validation.
