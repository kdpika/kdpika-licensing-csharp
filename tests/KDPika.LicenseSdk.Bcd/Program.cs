using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KDPika.LicenseSdk;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

var checks = 0;
void Assert(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
async Task Denied(Func<Task> action, string code) { try { await action(); throw new Exception("Expected " + code); } catch (CreditOperationException e) { Assert(e.ErrorCode == code, code + " got " + e.ErrorCode); } }
if(args.Contains("--credit-commerce")){
    using var f=new Fixture {Metered=true,Balance=0};Assert((await f.Client.ActivateAsync("FIXTURE")).Valid,"zero credit active");
    var grant=await f.Client.RedeemCreditsAsync("KDP100CR","code-grant-first");Assert(grant.GetProperty("added").GetInt32()==100&&grant.GetProperty("balance").GetInt64()==100,"signed grant overrides untrusted outer fields");
    Assert((await f.Client.RedeemCreditsAsync("KDP100CR","code-grant-first")).GetProperty("replayed").GetBoolean(),"safe replay");
    await Denied(async()=>{await f.Client.RedeemCreditsAsync("WRONGCODE","code-grant-bad");},"CREDIT_CODE_UNAVAILABLE");
    f.Uncertain=true;await Denied(async()=>{await f.Client.RedeemCreditsAsync("KDP100CR","code-grant-lost");},"NETWORK_ERROR");
    Assert((await f.Client.RedeemCreditsAsync("KDP100CR","code-grant-lost")).GetProperty("replayed").GetBoolean()&&f.Balance==200,"lost response recovered without double grant");
    Assert((await f.Client.CreditPacksAsync()).GetProperty("packs")[0].GetProperty("units").GetInt32()==100,"pack discovery");
    Assert((await f.Client.CreateCreditCheckoutAsync("cpk_fixture")).GetProperty("checkout_url").GetString()!.StartsWith("https://store.example.test/"),"checkout link");
    Assert((await f.Client.CreditHistoryAsync()).GetProperty("transactions").GetArrayLength()==0,"history");
    Console.WriteLine(JsonSerializer.Serialize(new{result="PASS",checks,network=false}));return;
}
var raw = new Dictionary<string,string> { ["baseboard"] = " BOARD-ABC ", ["cpu"] = "CPU-X", ["disk"] = "disk-123" };
var fp = HardwareFingerprint.FromComponents("app_test", raw, true);
var expectedBoard = Fixture.Hash(Fixture.Hash("kdpika-hardware-v1|app_test") + "|baseboard|board-abc");
Assert(fp.GetProperty("components").GetProperty("baseboard").GetString() == expectedBoard, "product-salted normalized hash");
Assert(!fp.GetRawText().Contains("BOARD-ABC") && fp.GetProperty("is_virtual").GetBoolean(), "no raw serials; virtual hint accepted");
Assert(fp.GetProperty("hardware_id").GetString() != HardwareFingerprint.FromComponents("other_app", raw).GetProperty("hardware_id").GetString(), "product-specific fingerprint");
try { HardwareFingerprint.FromComponents("app_test", new Dictionary<string,string> { ["cpu"] = "CPU-X", ["baseboard"] = "unknown" }); throw new Exception("CPU-only accepted"); } catch(CreditOperationException e) { Assert(e.ErrorCode == "HARDWARE_UNAVAILABLE", "no random fallback"); }
using(var f = new Fixture())
{
    var r = await f.Client.ActivateAsync("TEST-KEY", "customer@example.test"); Assert(r.Valid && r.Feature("export"), "signed activation");
    Assert(f.Activations == 1 && f.FingerprintsSent >= 1 && f.HeadersValid, "activation fingerprint and installation header");
    var cached = await f.Client.CheckAsync(); Assert(cached.Valid && cached.Source == "cache", "ordinary verified cache");
    f.Raw["disk"] = "changed-disk"; Assert((await f.Client.CheckAsync()).ErrorCode == "FINGERPRINT_MISMATCH", "strict hardware mismatch");
}
using(var f = new Fixture { Policy = "fuzzy" })
{
    Assert((await f.Client.ActivateAsync("TEST-KEY")).Valid, "fuzzy activate"); f.Raw["disk"] = "changed-disk";
    Assert((await f.Client.CheckAsync()).Valid, "fuzzy peripheral tolerance"); f.Raw["baseboard"] = "another-board";
    Assert((await f.Client.CheckAsync()).ErrorCode == "FINGERPRINT_MISMATCH", "fuzzy baseboard is mandatory");
}
using(var f = new Fixture { Floating = true })
{
    var r = await f.Client.ActivateAsync("TEST-KEY"); Assert(r.Valid && r.Feature("export") && f.Checkouts == 1, "automatic checkout");
    Assert(typeof(LicenseClient).GetField("heartbeatTimer", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(f.Client) is Timer, "automatic heartbeat scheduled");
    f.Now = f.Now.AddSeconds(61); Assert(!r.Feature("export"), "retained result obeys lease expiry");
    f.MissingSeat = true; Assert((await f.Client.HeartbeatAsync()).Valid && f.Checkouts == 2, "expired server session recovered once");
    f.Offline = true; f.Now = f.Now.AddSeconds(61); Assert((await f.Client.ValidateAsync()).ErrorCode == "SESSION_EXPIRED", "offline grace cannot extend seat");
    f.Offline = false; var fresh = await f.Client.CheckoutAsync(); Assert(fresh.Feature("export"), "fresh seat");
    f.Offline = true; Assert((await f.Client.CheckinAsync()).ErrorCode == "NETWORK_ERROR" && !fresh.Feature("export"), "checkin clears local grants before failure");
    Assert(typeof(LicenseClient).GetField("heartbeatTimer", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(f.Client) is null, "timer stopped on checkin");
}
using(var f = new Fixture { Floating = true, Full = true })
{
    Assert((await f.Client.ActivateAsync("TEST-KEY")).ErrorCode == "ALL_SEATS_IN_USE" && f.Client.Activated, "seat exhaustion keeps activation");
    await Denied(async()=>{await f.Client.CheckoutAsync();}, "ALL_SEATS_IN_USE");
}
using(var f = new Fixture { Floating = true, WrongLease = true })
{
    Assert((await f.Client.ActivateAsync("TEST-KEY")).ErrorCode == "SESSION_EXPIRED", "signed lease token binding");
}
using(var f = new Fixture { Metered = true, Balance = 0 })
{
    Assert((await f.Client.ActivateAsync("TEST-KEY")).Valid && f.Client.Activated, "zero-credit activation retained");
    Assert(await f.Client.GetBalanceAsync() == 0, "online zero balance");
    f.Balance = 5; Assert(await f.Client.GetBalanceAsync() == 5, "online top-up refresh");
    var spent = await f.Client.ConsumeAsync(5, "job-last-credit"); Assert(spent.Consumed == 5 && spent.Balance == 0 && !spent.Replayed, "last-credit charge is valid receipt");
    Assert((await f.Client.CheckAsync()).Feature("export"), "zero balance keeps included features");
    try { await f.Client.ConsumeAsync(1, "job-insufficient"); throw new Exception("overspend accepted"); } catch(InsufficientCreditsException e) { Assert(e.Balance == 0 && e.ErrorCode == "INSUFFICIENT_CREDITS", "typed verified insufficient balance"); }
}
using(var f = new Fixture { Metered = true, Balance = 10 })
{
    Assert((await f.Client.ActivateAsync("TEST-KEY")).Valid, "metered activate");
    var first = await f.Client.ConsumeAsync(3, "durable-job-1", "Export document"); await f.Client.ConsumeAsync(4, "durable-job-2");
    var replay = await f.Client.ConsumeAsync(3, "durable-job-1", "Export document"); Assert(replay.Replayed && replay.Balance == 7 && replay.TransactionId == first.TransactionId && f.Balance == 3, "signed historical replay without double charge");
    Assert(await f.Client.GetBalanceAsync() == 3, "fresh balance differs from historical receipt");
    await Denied(async()=>{await f.Client.ConsumeAsync(1, "durable-job-1");}, "IDEMPOTENCY_CONFLICT");
    Assert(await f.Client.GetBalanceAsync() == 3, "refresh after conflict"); f.Offline = true;
    Assert((await f.Client.CheckAsync()).ErrorCode == "METERED_ONLINE_REQUIRED", "metered cache never grants offline access");
    await Denied(async()=>{await f.Client.GetBalanceAsync();}, "METERED_ONLINE_REQUIRED");
    await Denied(async()=>{await f.Client.ConsumeAsync(1, "job-network-1");}, "NETWORK_ERROR");
    f.Offline = false; f.OuterTamper = true; await Denied(async()=>{await f.Client.ConsumeAsync(1, "job-tampered-1");}, "SIGNATURE_INVALID"); f.OuterTamper = false;
    f.Uncertain = true; var calls = f.Consumes; await Denied(async()=>{await f.Client.ConsumeAsync(1, "job-uncertain-1");}, "NETWORK_ERROR"); Assert(f.Consumes == calls+1, "uncertain charge not automatically retried");
    var recovered = await f.Client.ConsumeAsync(1, "job-uncertain-1"); Assert(recovered.Replayed && f.Balance == 1, "explicit safe retry with durable key");
    await Denied(async()=>{await f.Client.ConsumeAsync(0);}, "INVALID_CREDIT_AMOUNT");
    await Denied(async()=>{await f.Client.ConsumeAsync(9007199254740992);}, "INVALID_CREDIT_AMOUNT");
    await Denied(async()=>{await f.Client.ConsumeAsync(1,"tiny");}, "INVALID_REQUEST");
}
using(var f = new Fixture { WrongProduct = true }) { Assert((await f.Client.ActivateAsync("TEST-KEY")).ErrorCode == "SIGNATURE_INVALID", "cross-product signed payload rejected"); }
using(var f = new Fixture { WrongInstallation = true }) { Assert((await f.Client.ActivateAsync("TEST-KEY")).ErrorCode == "SIGNATURE_INVALID", "cross-installation signed payload rejected"); }
using(var f = new Fixture { Tamper = true }) { Assert((await f.Client.ActivateAsync("TEST-KEY")).ErrorCode == "SIGNATURE_INVALID", "signature tampering rejected"); }
using(var f = new Fixture { MissingFingerprint = true }) { Assert((await f.Client.ActivateAsync("TEST-KEY")).ErrorCode == "FINGERPRINT_MISMATCH", "no legacy fingerprint fallback"); }
Console.WriteLine(JsonSerializer.Serialize(new { result="PASS", checks, network=false, browser="unsupported" }));

sealed class Fixture : HttpMessageHandler
{
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    public Dictionary<string,string> Raw = new() { ["baseboard"]="BOARD-ABC", ["cpu"]="CPU-X", ["disk"]="disk-123" };
    public string Policy="strict"; public bool Floating, Metered, Offline, Full, MissingSeat, WrongLease, OuterTamper, Uncertain, WrongProduct, WrongInstallation, Tamper, MissingFingerprint;
    public long Balance=10; public int Activations, Checkouts, Consumes, FingerprintsSent; public bool HeadersValid=true;
    public LicenseClient Client {get;}
    private readonly string root = Path.Combine(Path.GetTempPath(),"kdpika-csharp-bcd-"+Guid.NewGuid().ToString("N"));
    private readonly Ed25519PrivateKeyParameters secret = new(new SecureRandom());
    private readonly Dictionary<string,(long Amount,long Balance,string Id)> jobs = new();
    private JsonElement binding;
    private string? session;
    private const string Seat = "flt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public Fixture() { Client = new LicenseClient("app_test","SANDBOX",verificationKey:new(){{"key_id","test"},{"public_key",B64(secret.GeneratePublicKey().GetEncoded())}},storageDir:root,httpClient:new HttpClient(this),clock:()=>Now,fingerprintProvider:p=>HardwareFingerprint.FromComponents(p,Raw,true)); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var path=request.RequestUri!.AbsolutePath; var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
        HeadersValid &= request.Headers.TryGetValues("X-Installation-ID",out var ids) && ids.Single()==Client.InstallationId;
        if(path.EndsWith("/consume"))Consumes++;
        if(Offline)throw new HttpRequestException("offline");
        if(body.TryGetProperty("fingerprint",out var fp)){FingerprintsSent++; if(fp.GetRawText().Contains("BOARD-ABC"))throw new Exception("raw hardware leaked");}
        if(path.EndsWith("/activate")) { Activations++; binding=body.GetProperty("fingerprint").Clone();return Reply(new{success=true,activation_token="act_test",license=Signed()}); }
        if(path.StartsWith("/api/v1/credits/")){
            if(path.EndsWith("/redeem")){
                if(body.GetProperty("code").GetString()!="KDP100CR")return Fail("CREDIT_CODE_UNAVAILABLE",422);
                var key=body.GetProperty("idempotency_key").GetString()!;var replayed=jobs.TryGetValue(key,out var saved);
                if(!replayed){Balance+=100;saved=(100,Balance,"ctx_code_grant");jobs[key]=saved;}
                var transaction=new{id=saved.Id,type="grant",amount=100,balance_after=saved.Balance,total_allocated_after=100,idempotency_hash=Hash("code:"+Hash(key)),replayed};
                if(Uncertain){Uncertain=false;throw new HttpRequestException("lost reply");}
                return Reply(new{success=true,license=Signed(transaction),transaction_id=saved.Id,added=999,balance=999,replayed});
            }
            return Reply(new{success=true,license=Signed(),packs=new[]{new{id="cpk_fixture",units=100}},checkout_url="https://store.example.test/?kdpika_credit_checkout=opaque",transactions=Array.Empty<object>()});
        }
        if(path.EndsWith("/deactivate"))return Reply(new{success=true,deactivated=true});
        if(path.EndsWith("/checkout")) { Checkouts++;if(Full)return Fail("ALL_SEATS_IN_USE",409);session=Seat;return Reply(new{success=true,session_token=session,license=Signed()}); }
        if(path.EndsWith("/heartbeat")) { if(MissingSeat){MissingSeat=false;session=null;return Fail("SESSION_NOT_FOUND",404);}return Reply(new{success=true,license=Signed()}); }
        if(path.EndsWith("/checkin")) { session=null;return Reply(new{success=true,checked_in=true}); }
        if(path.EndsWith("/validate"))return Reply(new{success=true,license=Signed()});
        if(path.EndsWith("/consume"))
        {
            var amount=body.GetProperty("amount").GetInt64();var key=body.GetProperty("idempotency_key").GetString();var saved=key is not null&&jobs.TryGetValue(key,out var previous)?previous:default;var replayed=saved.Id is not null;
            if(replayed&&saved.Amount!=amount)return Fail("IDEMPOTENCY_CONFLICT",409);
            if(!replayed&&Balance<amount)return Reply(new{success=false,error=new{code="INSUFFICIENT_CREDITS"},balance=Balance,license=Signed()},402);
            if(!replayed){Balance-=amount;saved=(amount,Balance,"ctx_"+Consumes);if(key is not null)jobs[key]=saved;}
            var transaction=new{id=saved.Id,type="consume",amount=-amount,balance_after=saved.Balance,total_allocated_after=10,idempotency_hash=key is null?null:Hash(key),replayed};
            if(Uncertain){Uncertain=false;throw new HttpRequestException("response lost after commit");}
            return Reply(new{success=true,license=Signed(transaction),transaction_id=saved.Id,consumed=amount,balance=saved.Balance+(OuterTamper?1:0),replayed});
        }
        throw new Exception("Unexpected request");
    }
    private object Signed(object? transaction=null)
    {
        var parts=binding.GetProperty("components");
        var fingerprint=MissingFingerprint?null:new{version=1,hardware_id=binding.GetProperty("hardware_id").GetString(),components=parts,policy=new{mode=Policy,min_matches=2}};
        object? floating=Floating?new{max_concurrent_sessions=1,heartbeat_interval_seconds=30,session=session is null?null:new{token_hash=Hash(WrongLease?"wrong":session),expires_at=Now.AddSeconds(60).ToString("O")}}:null;
        var payload=new{product_id=WrongProduct?"other_app":"app_test",environment="SANDBOX",license_id="lic_test",status="active",features=new{export=true},device=new{installation_hash=Hash(WrongInstallation?"other-install":Client.InstallationId),fingerprint},server_time=Now.ToString("O"),next_validation_at=Now.AddHours(24).ToString("O"),offline_grace_until=Now.AddDays(7).ToString("O"),expires_at=(string?)null,floating,metered=Metered,credit_balance=Balance,total_allocated=10,credit_transaction=transaction};
        var json=JsonSerializer.Serialize(payload);var bytes=Encoding.UTF8.GetBytes(json);var signer=new Ed25519Signer();signer.Init(true,secret);signer.BlockUpdate(bytes,0,bytes.Length);var signature=signer.GenerateSignature();if(Tamper)signature[0]^=1;
        return new{payload,payload_json=json,signature=B64(signature),algorithm="Ed25519",key_id="test"};
    }
    private static HttpResponseMessage Reply(object body,int status=200)=>new((HttpStatusCode)status){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Fail(string code,int status)=>Reply(new{success=false,error=new{code}},status);
    public static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static string B64(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
    protected override void Dispose(bool disposing) { if(disposing){Client.CloseAsync().GetAwaiter().GetResult();if(Directory.Exists(root))Directory.Delete(root,true);}base.Dispose(disposing); }
}
