# Third-party notices

The project owner's code and linguistic data are covered by the root MIT license.
The .NET SDK/runtime and test tooling retain their own licenses. The public source
distribution does not vendor NuGet packages, SDK binaries or GitHub action code.

The public test target reuses the project's existing Microsoft.NET.Test.Sdk18.7.0
and MSTest.TestAdapter/TestFramework4.2.3. Its exact direct/transitive versions and
content hashes are recorded in `tests/BgLang.Public.Tests/packages.lock.json`.
Restore obtains their original package payloads and notices.

The CI workflow pins [actions/checkout v4.2.2](https://github.com/actions/checkout/releases/tag/v4.2.2)
and [actions/setup-dotnet v4.3.1](https://github.com/actions/setup-dotnet/releases/tag/v4.3.1)
to their verified release commits. Their upstream license files remain authoritative.

Resolved public test packages (license metadata from their exact restored nuspecs):

| Package | Version | Upstream license metadata |
|---|---|---|
| Microsoft.ApplicationInsights | 2.23.0 | MIT |
| Microsoft.CodeCoverage | 18.7.0 | MIT |
| Microsoft.NET.Test.Sdk | 18.7.0 | MIT |
| Microsoft.Testing.Extensions.Telemetry | 2.2.3 | MIT |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.2.3 | MIT |
| Microsoft.Testing.Extensions.VSTestBridge | 2.2.3 | MIT |
| Microsoft.Testing.Platform | 2.2.3 | MIT |
| Microsoft.Testing.Platform.MSBuild | 2.2.3 | MIT |
| Microsoft.TestPlatform.ObjectModel | 18.7.0 | MIT |
| Microsoft.TestPlatform.TestHost | 18.7.0 | MIT |
| MSTest.Analyzers | 4.2.3 | MIT |
| MSTest.TestAdapter | 4.2.3 | MIT |
| MSTest.TestFramework | 4.2.3 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
