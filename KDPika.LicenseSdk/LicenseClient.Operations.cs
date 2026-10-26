using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KDPika.LicenseSdk;

public sealed partial class LicenseClient
{
    // Serialize state and credit operations for this installation, including other client instances.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private Timer? heartbeatTimer;
    public string? LastFloatingError { get; private set; }

    private async Task<T> SynchronizedAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(storageDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        FileStream? fileLock = null;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (fileLock is null)
            {
                try { fileLock = new FileStream(Path.Combine(storageDir, ".operations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); TryUnixMode(Path.Combine(storageDir, ".operations.lock"), UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (IOException) { if (DateTime.UtcNow >= deadline) throw new CreditOperationException("STORAGE_BUSY"); await Task.Delay(25, cancellationToken); }
            }
            return await work();
        }
        finally { fileLock?.Dispose(); gate.Release(); }
    }
    private Dictionary<string,string> AuthHeaders(string token) => new() { { "Authorization", "Bearer " + token }, { "X-Installation-ID", InstallationId } };
    private string? SessionToken()
    {
        var state = ReadJson("floating-session.json");
        return state.ValueKind == JsonValueKind.Object && state.TryGetProperty("session_token", out var t) && t.ValueKind == JsonValueKind.String && Regex.IsMatch(t.GetString()!, @"\Aflt_[A-Za-z0-9_-]{43}\z") ? t.GetString() : null;
    }
    private void StopHeartbeat() { heartbeatTimer?.Dispose(); heartbeatTimer = null; }
    private void StartHeartbeat(JsonElement policy)
    {
        if (heartbeatTimer is not null || SessionToken() is null) return;
        var interval = TimeSpan.FromSeconds(policy.GetProperty("heartbeat_interval_seconds").GetInt32());
        var heldSession = SessionToken();
        var weak = new WeakReference<LicenseClient>(this);
        // A one-shot callback schedules the next heartbeat only after the previous one completes.
        heartbeatTimer = new Timer(async state =>
        {
            if (state is not WeakReference<LicenseClient> reference || !reference.TryGetTarget(out var client)) return;
            try { var result = await client.SynchronizedAsync(async () => client.heartbeatTimer is null || client.SessionToken() != heldSession ? null : await client.SeatInnerAsync("heartbeat", CancellationToken.None), CancellationToken.None); client.LastFloatingError = result?.ErrorCode; }
            catch { client.LastFloatingError = "NETWORK_ERROR"; }
            try { client.heartbeatTimer?.Change(interval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }, weak, interval, Timeout.InfiniteTimeSpan);
    }
    public Task<LicenseResult> CheckoutAsync(CancellationToken cancellationToken = default) => SynchronizedAsync(async () => { var r = await SeatInnerAsync("checkout", cancellationToken); if (r.ErrorCode == "ALL_SEATS_IN_USE") throw new AllSeatsInUseException(); return r; }, cancellationToken);
    public Task<LicenseResult> HeartbeatAsync(CancellationToken cancellationToken = default) => SynchronizedAsync(() => SeatInnerAsync("heartbeat", cancellationToken), cancellationToken);
    public Task<LicenseResult> CheckinAsync(CancellationToken cancellationToken = default) => SynchronizedAsync(() => SeatInnerAsync("checkin", cancellationToken), cancellationToken);
    public Task<LicenseResult> CloseAsync(CancellationToken cancellationToken = default) => CheckinAsync(cancellationToken);
    public async ValueTask DisposeAsync() { StopHeartbeat(); if (SessionToken() is not null) { try { await CheckinAsync(); } catch { } } GC.SuppressFinalize(this); }

    private async Task<LicenseResult> SeatInnerAsync(string action, CancellationToken cancellationToken)
    {
        var token = ReadText("activation-token"); var session = SessionToken();
        if (action == "checkin") { StopHeartbeat(); Remove("floating-session.json"); Remove("signed-state.json"); }
        if (token is null) return Error("NOT_ACTIVATED", "Activate this installation first.", "local");
        if (action != "checkout" && session is null) return action == "heartbeat" ? await SeatInnerAsync("checkout", cancellationToken) : new(true, "online", EmptyPayload(), Message: "No shared seat is held.");
        JsonElement fingerprint; try { fingerprint = fingerprintProvider(product); } catch { return Error("HARDWARE_UNAVAILABLE", "Device information is unavailable.", "local"); }
        ApiResponse response;
        try { response = await RequestAsync(HttpMethod.Post, "/api/v1/licenses/" + action, new { fingerprint, session_token = session }, AuthHeaders(token), cancellationToken); }
        catch { return Error("NETWORK_ERROR", "Connect to manage your shared seat.", "local"); }
        if (!response.Ok)
        {
            StopHeartbeat(); Remove("floating-session.json"); Remove("signed-state.json"); var r = ResponseError(response);
            return action == "heartbeat" && r.ErrorCode == "SESSION_NOT_FOUND" ? await SeatInnerAsync("checkout", cancellationToken) : r;
        }
        if (action == "checkin") return response.Body.TryGetProperty("checked_in", out var done) && done.ValueKind == JsonValueKind.True ? new(true, "online", EmptyPayload(), Message: "Shared seat released.") : Error("INVALID_RESPONSE", "Seat release was not confirmed.", "online", response);
        if (action == "checkout")
        {
            if (!response.Body.TryGetProperty("session_token", out var t) || t.ValueKind != JsonValueKind.String || !Regex.IsMatch(t.GetString()!, @"\Aflt_[A-Za-z0-9_-]{43}\z")) return Error("INVALID_RESPONSE", "The shared seat could not be verified.", "online", response);
            WriteJson("floating-session.json", new { session_token = t.GetString() });
        }
        JsonElement signed = default; LicenseResult result;
        try { signed = response.Body.GetProperty("license"); result = await VerifiedAsync(signed, "online", response, cancellationToken); }
        catch { result = Error("SIGNATURE_INVALID", "The shared seat could not be verified.", "online", response); }
        if ((result.Valid || result.ErrorCode == "INSUFFICIENT_CREDITS") && result.Payload.GetProperty("floating").ValueKind==JsonValueKind.Object) { await SaveState(signed, cancellationToken); StartHeartbeat(result.Payload.GetProperty("floating")); }
        else if(result.Valid) { StopHeartbeat();Remove("floating-session.json");Remove("signed-state.json");return Error("INVALID_RESPONSE","The shared seat could not be verified.","online",response); }
        else { StopHeartbeat(); Remove("floating-session.json"); Remove("signed-state.json"); }
        return result;
    }
    public Task<long> GetBalanceAsync(CancellationToken cancellationToken = default) => SynchronizedAsync(async () =>
    {
        var r = await CheckInnerAsync(true, cancellationToken);
        if (!r.SignatureValid || (!r.Valid && r.ErrorCode != "INSUFFICIENT_CREDITS")) throw new CreditOperationException(r.ErrorCode ?? "INVALID_RESPONSE");
        if (!r.Payload.GetProperty("metered").GetBoolean()) throw new CreditOperationException("NOT_METERED_LICENSE");
        return r.Payload.GetProperty("credit_balance").GetInt64();
    }, cancellationToken);

    /// <summary>No automatic retry. Persist one idempotency key per business operation before sending.</summary>
    public Task<CreditConsumption> ConsumeAsync(long amount, string? idempotencyKey = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (amount < 1 || amount > LicensePolicy.MaxCredits) throw new CreditOperationException("INVALID_CREDIT_AMOUNT");
        if ((idempotencyKey is not null && !Regex.IsMatch(idempotencyKey, @"\A[A-Za-z0-9_.:-]{8,190}\z")) || (reason is not null && (reason.EnumerateRunes().Count() > 190 || Regex.IsMatch(reason, @"[\x00-\x1f\x7f]")))) throw new CreditOperationException("INVALID_REQUEST");
        return SynchronizedAsync(async () =>
        {
            var token = ReadText("activation-token"); if (token is null) throw new CreditOperationException("NOT_ACTIVATED");
            JsonElement fingerprint; try { fingerprint = fingerprintProvider(product); } catch { throw new CreditOperationException("HARDWARE_UNAVAILABLE"); }
            ApiResponse response;
            try { response = await RequestAsync(HttpMethod.Post, "/api/v1/licenses/consume", new { amount, idempotency_key = idempotencyKey, reason, fingerprint, session_token = SessionToken() }, AuthHeaders(token), cancellationToken); }
            catch { throw new CreditOperationException("NETWORK_ERROR"); }
            if (!response.Ok && ResponseError(response).ErrorCode != "INSUFFICIENT_CREDITS") { Remove("signed-state.json"); throw new CreditOperationException(ResponseError(response).ErrorCode ?? "HTTP_ERROR"); }
            JsonElement signed; LicenseResult r;
            try { signed = response.Body.GetProperty("license"); r = await VerifiedAsync(signed, "online", response, cancellationToken); }
            catch { throw new CreditOperationException("SIGNATURE_INVALID"); }
            if (!r.SignatureValid || (!r.Valid && r.ErrorCode != "INSUFFICIENT_CREDITS")) throw new CreditOperationException(r.ErrorCode ?? "SIGNATURE_INVALID");
            if (!r.Payload.GetProperty("metered").GetBoolean()) throw new CreditOperationException("NOT_METERED_LICENSE");
            if (!response.Ok)
            {
                var balance = r.Payload.GetProperty("credit_balance").GetInt64();
                if (response.Status != 402 || balance >= amount || !response.Body.TryGetProperty("balance", out var b) || !LicensePolicy.Integer(b) || b.GetInt64() != balance) throw new CreditOperationException("SIGNATURE_INVALID");
                await SaveState(signed, cancellationToken); throw new InsufficientCreditsException(balance);
            }
            CreditConsumption receipt;
            try
            {
                var c = r.Payload.GetProperty("credit_transaction"); var body = response.Body;
                var id = c.GetProperty("id").GetString()!; var balance = c.GetProperty("balance_after"); var allocated = c.GetProperty("total_allocated_after");
                var replayed = c.GetProperty("replayed").GetBoolean();
                if (c.GetProperty("type").GetString() != "consume" || !Regex.IsMatch(id, @"\Actx_[A-Za-z0-9_-]{1,36}\z") || c.GetProperty("amount").GetInt64() != -amount || !LicensePolicy.Integer(balance) || !LicensePolicy.Integer(allocated) || c.GetProperty("idempotency_hash").GetString() != (idempotencyKey is null ? null : HardwareFingerprint.Hash(idempotencyKey)) || body.GetProperty("transaction_id").GetString() != id || body.GetProperty("consumed").GetInt64() != amount || body.GetProperty("balance").GetInt64() != balance.GetInt64() || body.GetProperty("replayed").GetBoolean() != replayed) throw new InvalidDataException();
                receipt = new(id, amount, balance.GetInt64(), replayed);
            }
            catch { throw new CreditOperationException("SIGNATURE_INVALID"); }
            await SaveState(signed, cancellationToken); return receipt;
        }, cancellationToken);
    }
}
