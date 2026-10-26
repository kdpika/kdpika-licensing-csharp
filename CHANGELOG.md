# Current release: 2.0.0

- Required signed hardware binding, floating checkout/heartbeat/checkin and online idempotent usage credits.
- Updated installation, source links and cross-language capability documentation.
- Customer portal remains hosted; browser hardware fallback is unsupported.

# Package observations and documentation changes

## 2.0.0 — 2026-10-26

- Retarget the library, focused tests and examples to .NET 10 LTS; no licensing or signature changes.

- Require product-salted hardware hashes and verify signed device/installation binding.
- Add floating checkout, heartbeat and checkin with signed lease expiry and seat exhaustion.
- Add online-only credit balance/consume, idempotent receipts and insufficient-credit errors.
- Breaking pre-launch server contract; older published clients are not compatible.
- Registry availability is recorded separately; a changelog is not proof of publication. Ed25519 is unchanged.

## Unreleased — 2026-10-25

- Require hashed native hardware fingerprints and verify signed strict/fuzzy binding; no legacy or browser fallback.
- Add floating checkout/heartbeat/checkin, background renewal, signed lease guards and typed seat exhaustion.
- Add online-only metered balance/consume, verified idempotent receipts and typed insufficient-credit errors; no automatic charge retries.
- Serialize installation operations, revoke local seat grants before checkin, and add focused synthetic B/C/D cases. Ed25519 and server code unchanged.

## 1.0.1 — 2026-10-21

- Guard features with validity, signature and signed deadlines; invalid results ignore enabling defaults. Positive numeric flags match Python semantics.
- Verify additive `payload_json` exact signed bytes and use their decoded fields as authoritative, including after cached Unicode is re-serialized.
- Clear stale cached access on malformed/rejected validation; require confirmed deactivation. Default HTTP client does not follow redirects; custom clients must enforce the same policy.
- Existing envelopes retain canonical fallback; refresh online to replace an incompatible legacy cache. No new dependency.

## 1.0.0 — observed current source, 2026-10-17

This is the version recorded in current package/project metadata (PHP uses the
existing versioned download label). It is not a reconstructed original release
date, a published registry release or an invented earlier-version history.

- Added complete source-install, lifecycle, storage/offline and known-limit
  documentation, license-information notice and runnable example links.
- Preserved runtime implementation, public APIs, response handling and package
  identity. Documentation changes do not silently fix known signing/cache gaps.
- PHP includes a new package-local standalone test entry; its legacy repository
  test remains unchanged and still assumes the repository-root autoload layout.

Future entries must identify the exact version, verified changes, compatibility
impact and tests. API v1 and SDK package versions are independent.
