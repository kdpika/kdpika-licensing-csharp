// Isolated local result/member exercise. No payment/API/credential request.
using System.Text.Json;
using KDPika.LicenseSdk;

var covered = new List<string>();
void Check(string member, bool condition) {
    if (!condition) throw new InvalidOperationException("Member example failed: " + member);
    covered.Add(member);
}
var temporary = Path.Combine(Path.GetTempPath(), "kdpika-members-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try {
    var client = new LicenseClient("YOUR_APP_ID", "SANDBOX", storageDir: temporary);
    Check("LicenseClient.LicenseClient", client is not null);
    Check("LicenseClient.InstallationId", client!.InstallationId.StartsWith("lmp-install-", StringComparison.Ordinal));
    Check("LicenseClient.Activated", !client.Activated);
    Check("LicenseClient.ActivateAsync", (await client.ActivateAsync("")).ErrorCode == "INVALID_LICENSE");
    Check("LicenseClient.CheckAsync", (await client.CheckAsync()).ErrorCode == "NOT_ACTIVATED");
    Check("LicenseClient.ValidateAsync", (await client.ValidateAsync()).ErrorCode == "NOT_ACTIVATED");
    Check("LicenseClient.DeactivateAsync", (await client.DeactivateAsync()).ErrorCode == "NOT_ACTIVATED");
    // Constructing this record does NOT verify the payload or grant access.
    var payload = JsonSerializer.SerializeToElement(new { features = new { pdf_export = true, seat_count = 2 } });
    var result = new LicenseResult(false, "example", payload, "EXAMPLE_ONLY", "Example", "req_example", false, "unknown");
    Check("LicenseResult.LicenseResult", result is not null);
    Check("LicenseResult.Valid", !result!.Valid);
    Check("LicenseResult.Source", result.Source == "example");
    Check("LicenseResult.Payload", result.Payload.GetProperty("features").GetProperty("seat_count").GetInt32() == 2);
    Check("LicenseResult.ErrorCode", result.ErrorCode == "EXAMPLE_ONLY");
    Check("LicenseResult.Message", result.Message == "Example");
    Check("LicenseResult.RequestId", result.RequestId == "req_example");
    Check("LicenseResult.SignatureValid", !result.SignatureValid);
    Check("LicenseResult.StorageSecurity", result.StorageSecurity == "unknown");
    Check("LicenseResult.Feature", result.Feature("pdf_export") && !result.Feature("missing"));
    Check("LicenseResult.FeatureValue", result.FeatureValue("seat_count")?.GetInt32() == 2 && result.FeatureValue("missing") is null);
    Check("application.fail_closed", !(result.Valid && result.SignatureValid && result.Feature("pdf_export")));
    Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", members = covered,
        mode = "synthetic local objects; zero HTTP", access_granted = false }));
} finally {
    // Only this exact fresh scratch directory is removed, never user state.
    Directory.Delete(temporary, recursive: true);
}
