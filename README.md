# KDPika Licensing C# SDK

Official SDK **2.0.0** for the current KDPika API contract.

[Documentation](https://licensing.kdpika.com/docs/v1/sdks/csharp) · [Download SDK ZIP](https://licensing.kdpika.com/docs/downloads/kdpika-csharp-sdk-2.0.0.zip)

## Supported licensing capabilities

| Capability | Behavior |
|---|---|
| Signed checks and offline access | Verify Ed25519 results; access never extends beyond signed expiry/offline policy. |
| Device matching | Required product-salted hardware hashes and X-Installation-ID; verify the signed device binding. Raw serials are not sent. |
| Floating licenses | Checkout, heartbeat and checkin with signed lease deadlines; handle ALL_SEATS_IN_USE. |
| Usage credits | Online-only consume with a persisted idempotency key; query balance and handle INSUFFICIENT_CREDITS. |
| Customer portal | Hosted self-service portal managed by the server, not an SDK payment or account feature. |

Advanced matching, floating seats, credits and portal availability depend on your workspace plan. Browser/WASM hardware fingerprint fallback is unsupported. Test on your target OS and in Sandbox before distributing your app.

## Install

```sh
dotnet add package KDPika.LicenseSdk --version 2.0.0 --source ./packages
```

For JavaScript and C#, local package/source downloads are available; npm/NuGet publication is separate and must be confirmed before using a registry-only install command.


## Hardware, shared seats and credits

Activation and validation require a product-salted hardware fingerprint and
`X-Installation-ID`. Windows, macOS and Linux collectors send hashes only; unavailable
hardware fails with `HARDWARE_UNAVAILABLE`. Signed strict/fuzzy device policy is
enforced before access. `is_virtual` is informational. A trusted native adapter can
provide the optional `fingerprintProvider`; never accept fingerprints from an end user.
Browser/WebAssembly use is unsupported; there is no browser fallback.

Floating licenses reserve a seat automatically on activation. Use `CheckoutAsync`,
`HeartbeatAsync` and `CheckinAsync` for explicit lifecycle management. A background
timer renews the signed lease; `CloseAsync`/`await using` releases it. An application
crash leaves the server to expire the lease. `AllSeatsInUseException` identifies an
explicit checkout refusal; activation returns `ALL_SEATS_IN_USE` in its result.
Feature helpers recheck the lease, including previously returned results. Offline
grace never extends a seat. Check `LastFloatingError` to diagnose heartbeat failures.

For metered licenses, `GetBalanceAsync()` always checks online. Call
`ConsumeAsync(amount, idempotencyKey, reason)` before the corresponding paid operation.
Persist one idempotency key per business job **before** sending, then reuse it when
explicitly retrying an uncertain response. The SDK never retries charges automatically.
`InsufficientCreditsException.Balance` is a verified balance. A successful charge
of the last credit still returns `CreditConsumption`; zero balance denies new access.
Replay receipts report the historical balance after that charge; fetch the current
balance separately. Business-job execution itself needs your own deduplication.
Credit amounts are positive whole numbers up to 9,007,199,254,740,991. Cached metered
licenses return `METERED_ONLINE_REQUIRED`. No credit allocation or browser API is added.

Focused package check: `dotnet run --project tests/KDPika.LicenseSdk.Bcd/KDPika.LicenseSdk.Bcd.csproj`.
This uses synthetic signed fixtures; it does not certify live-server or cross-OS behavior.

```bash
tar -xzf kdpika-csharp-sdk-1.0.1.tar.gz
dotnet add reference ./kdpika-csharp-sdk/KDPika.LicenseSdk/KDPika.LicenseSdk.csproj
```

```csharp
using KDPika.LicenseSdk;

var client = new LicenseClient("YOUR_PUBLIC_APP_ID", "SANDBOX", applicationName: "Your App");
var result = await client.CheckAsync();
if (!result.Valid && result.ErrorCode == "NOT_ACTIVATED") result = await client.ActivateAsync("YOUR-LICENSE-KEY", "customer@example.com");
if (!result.Valid) throw new Exception($"{result.ErrorCode}: {result.Message}");
```

The package uses Bouncy Castle 2.7.0 for Ed25519 verification and stores state in the per-user application-data directory with user-only permissions where the OS supports them. Desktop apps may replace this layer with Keychain, Credential Manager, or Secret Service hardening.

## Release limitations and trust boundary

Version 1.0.1 verifies exact `payload_json` signed bytes, including after cached Unicode is re-serialized. Feature helpers reject invalid, unsigned or out-of-date decisions. Keep an explicit validity/signature gate. Older envelopes retain canonical fallback: refresh an incompatible legacy cache online. Never bypass verification. Historical signing-key overlap is not implemented.

Your own store handles software-buyer payments. KDPika Licensing bills only your developer platform subscription. Package/import names remain unchanged for compatibility. No registry publication is implied by this local-download README. The short host-application excerpts are not a complete application; the upcoming documentation phases must supply executed, standalone samples before treating them as a quickstart.

Contact: [hi@kdpika.com](mailto:hi@kdpika.com). [Current documentation](https://licensing.kdpika.com/documentation), [security controls](https://licensing.kdpika.com/#security), [service health check](https://licensing.kdpika.com/healthz).

<!-- P7-QUALITY:START -->
## Complete integration guide and source distribution

Read the complete guide at https://licensing.kdpika.com/docs/v1/sdks/csharp.
The public documentation describes actual source versions, local install steps,
every constructor/member, synchronous/async behavior, storage, offline policy,
logging, framework integration, packaging, upgrades and removal. Downloads are
source packages with SHA-256 checksums; registry publication is not verified.

Use Sandbox explicitly in tests. An App ID is public; lmp_ developer keys belong
only on your trusted backend. Keep a random stable installation ID and private
state outside executable extraction/public web directories. A token-presence
property, HTTP200, true feature helper or successful deactivation is not a
current access grant. Require valid and signature verification before features.

### Current release limitations

Python 1.1.5 and PHP/JavaScript/C# 1.0.1 include the Phase 1 signed-response
and feature-guard protections. PHP requires8.2+; PHP8.5 can emit curl_close
deprecation. Old1.0.0 caches may require an online refresh. Never disable
Ed25519, strip signed fields or extend offline permission.
The complete guides provide source-derived boundaries and test vectors.

Python can prefer an installed usable OS keyring; Node/PHP/C# use permission-
protected files, not automatic encrypted vaults. Local machine owners can patch
an app. No universal OS/anti-tamper/security-certification guarantee is made.

### Package tests and examples

The examples folder contains exact documented session/member sources plus layout
instructions. Existing package tests use synthetic signing/transports; run real
Sandbox lifecycle and public verification vectors as separate acceptance tests.

Run `dotnet run --project tests/KDPika.LicenseSdk.Parity/KDPika.LicenseSdk.Parity.csproj` with a compatible runtime.

See LICENSE for existing license-information boundaries and CHANGELOG.md for verified observations; neither implies an invented public registry release.
<!-- P7-QUALITY:END -->

## Target runtime update

The library, focused test projects and examples target .NET 10 LTS. Build and run with the .NET 10 SDK/runtime; .NET 7 and .NET 8 applications need a runtime migration before referencing this target. The final private release candidate was rebuilt for net10.0. Earlier Part 3 net7.0 artifacts are superseded and must not be published. No package has been published.
