# Release Roadmap Triage

This private note defines how ChokaQ roadmap files are sorted for the first
NuGet preview.

The goal of the first NuGet release is to publish a credible package, learn
whether people try it, and avoid shipping known correctness bugs or misleading
claims. It is not to implement every staff-level or hyperscale hardening idea
before anyone has used the package.

## Sorting Rules

## High

Fix before the first NuGet preview, or explicitly reduce/remove the public
claim.

High means at least one of these is true:

- The issue can corrupt job state or finalization.
- The issue can execute the wrong job type or deserialize with an unsafe
  persisted contract.
- The issue can make a documented feature materially false.
- The issue can create duplicate side effects beyond ChokaQ's documented
  at-least-once contract.
- The issue can leave core execution stuck in normal, non-hyperscale usage.
- The issue affects security or destructive operations.

Current high roadmaps:

None.

## Middle

Important after preview, or before a more serious production/stable release.
These are real engineering concerns, but not blockers for learning whether the
package has interest.

Middle means at least one of these is true:

- The issue is a real performance or maintainability boundary, but current
  correctness is acceptable.
- The issue appears under higher load or more complex deployments.
- The issue improves API polish, diagnostics, or extension quality.
- The issue should be tested before marketing stronger production claims.

Current middle roadmaps:

None.

## Low

Defer until after release feedback unless a specific user request proves demand.

Low means at least one of these is true:

- The issue is primarily operator UX polish.
- The issue is SRE maturity beyond the preview promise.
- The issue is useful for scale, audit, or incident response, but the current
  behavior is safe enough for early adopters.
- The issue is a nice feature rather than a bug.

Current low roadmaps:

- `low/db-maintenance-roadmap.md`
- `low/deterministic-replay-roadmap.md`
- `low/operator-concurrency-control-roadmap.md`
- `low/self-tuning-concurrency-roadmap.md`
- `low/sql-storage-scale-hardening-roadmap.md`
- `low/sre-hardening-roadmap.md`
- `low/state-manager-outbox-roadmap.md`
- `low/the-deck-ui-hardening-roadmap.md`

## Practical Release Rule

If a high item is too large to implement before preview, either:

- narrow the public claim,
- mark the feature experimental,
- hide it from public docs,
- or document the limitation clearly in the README.

Do not block the first NuGet preview on low-priority roadmap items.
