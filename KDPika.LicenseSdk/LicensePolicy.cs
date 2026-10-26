using System.Text.Json;

namespace KDPika.LicenseSdk;

internal static class LicensePolicy
{
    internal const long MaxCredits = 9007199254740991;
    internal static bool Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 && n <= MaxCredits;
    internal static bool Credits(JsonElement p) => p.TryGetProperty("metered", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False && p.TryGetProperty("credit_balance", out var b) && Integer(b) && p.TryGetProperty("total_allocated", out var a) && Integer(a);
    internal static bool Lease(JsonElement p, string? token, DateTimeOffset now)
    {
        try
        {
            var f = p.GetProperty("floating"); if (f.ValueKind == JsonValueKind.Null) return true;
            return f.GetProperty("max_concurrent_sessions").TryGetInt32(out var count) && count > 0 && token is not null && f.GetProperty("session").GetProperty("token_hash").GetString() == HardwareFingerprint.Hash(token) && DateTimeOffset.TryParse(f.GetProperty("session").GetProperty("expires_at").GetString(), out var deadline) && now < deadline;
        }
        catch { return false; }
    }
}

public class CreditOperationException : Exception
{
    public string ErrorCode { get; }
    public CreditOperationException(string code) : base("The license operation could not be completed (" + code + ").") { ErrorCode = code; }
}
public sealed class InsufficientCreditsException : CreditOperationException
{
    public long Balance { get; }
    public InsufficientCreditsException(long balance) : base("INSUFFICIENT_CREDITS") { Balance = balance; }
}
public sealed class AllSeatsInUseException : CreditOperationException
{
    public AllSeatsInUseException() : base("ALL_SEATS_IN_USE") { }
}
public sealed record CreditConsumption(string TransactionId, long Consumed, long Balance, bool Replayed);
