# NuGet Trusted Publishing setup

This repository uses a manual-only workflow. Adding this file, pushing commits or creating a tag does not trigger publishing. No permanent NuGet API key is needed.

Configure the policy at https://www.nuget.org/account/TrustedPublishing:

| Field | Value |
| --- | --- |
| Policy name | `kdpika-csharp-nuget` (display name; you may change it) |
| Package owner | `kdpika` |
| CI/CD provider | GitHub Actions |
| Repository owner | `kdpika` |
| Repository | `kdpika-licensing-csharp` |
| Workflow file | `publish-nuget.yml` (file name only) |
| Environment | `nuget` |
| Push scope | Push new packages and package versions |
| Glob patterns and packages | `KDPika.LicenseSdk` (exact ID; no wildcard) |

Leave unlist/relist permission disabled. This is the first NuGet publication; a scope that permits only new versions of an existing package is insufficient.

When publication is separately authorized, open the workflow on the `main` branch. Enter an existing reviewed release-tag version, such as `2.1.0`, and confirmation `publish KDPika.LicenseSdk`. The build reads that existing tag, checks the identity and target, and prepares one package. The `nuget` environment requires owner approval before OIDC authentication and upload. The approved artifact's checksum is checked again before upload. The workflow does not create GitHub releases or tags.

Documentation: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
