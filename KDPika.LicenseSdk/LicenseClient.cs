using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace KDPika.LicenseSdk;

public sealed partial class LicenseClient : IAsyncDisposable
{
    private readonly string product, environment, baseUrl, applicationName, applicationVersion, storageDir;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string,JsonElement> fingerprintProvider;
    private Dictionary<string,string>? verificationKey;
    public string InstallationId { get; }
    public bool Activated => ReadText("activation-token") is not null;

    public LicenseClient(string product, string environment = "LIVE", string baseUrl = "https://licensing.kdpika.com", string applicationName = "Licensed Application", string applicationVersion = "", Dictionary<string,string>? verificationKey = null, string? storageDir = null, HttpClient? httpClient = null, Func<DateTimeOffset>? clock = null, Func<string,JsonElement>? fingerprintProvider = null)
    {
        if (string.IsNullOrWhiteSpace(product)) throw new ArgumentException("A Public App ID is required.");
        if (!SafeBaseUrl(baseUrl)) throw new ArgumentException("The API base URL must use HTTPS; loopback HTTP is allowed for development.");
        environment = environment.Trim().ToUpperInvariant() switch { "PRODUCTION" => "LIVE", "TEST" => "SANDBOX", var value => value };
        if (environment is not ("SANDBOX" or "LIVE")) throw new ArgumentException("Environment must be SANDBOX or LIVE.");
        if (verificationKey is not null && (!verificationKey.ContainsKey("key_id") || !verificationKey.ContainsKey("public_key"))) throw new ArgumentException("Pinned verification key is incomplete.");
        this.product=product.Trim();this.environment=environment;this.baseUrl=baseUrl.TrimEnd('/');this.applicationName=applicationName;this.applicationVersion=applicationVersion;this.verificationKey=verificationKey;this.clock=clock??(()=>DateTimeOffset.UtcNow);
        this.fingerprintProvider=fingerprintProvider??HardwareFingerprint.Collect;
        http=httpClient??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
        var ns=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{this.baseUrl.ToLowerInvariant()}|{this.product}|{this.environment}"))).ToLowerInvariant()[..20];
        this.storageDir=Path.Combine(storageDir??DefaultStorageDir(applicationName),ns);Directory.CreateDirectory(this.storageDir);
        TryUnixMode(this.storageDir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var identity=ReadJson("installation.json");
        var currentId=identity.ValueKind==JsonValueKind.Object&&identity.TryGetProperty("installation_id",out var current)&&current.ValueKind==JsonValueKind.String?current.GetString():null;
        var hasIdentity=!string.IsNullOrWhiteSpace(currentId);
        InstallationId=hasIdentity?currentId!:"lmp-install-"+Guid.NewGuid();
        if(!hasIdentity)WriteJson("installation.json",new{installation_id=InstallationId});
    }

    public Task<LicenseResult> ActivateAsync(string licenseKey,string? email=null,CancellationToken cancellationToken=default)=>SynchronizedAsync(()=>ActivateInnerAsync(licenseKey,email,cancellationToken),cancellationToken);
    private async Task<LicenseResult> ActivateInnerAsync(string licenseKey,string? email,CancellationToken cancellationToken)
    {
        if(string.IsNullOrWhiteSpace(licenseKey))return Error("INVALID_LICENSE","Enter a license key.","local");
        var body=new Dictionary<string,object?>{{"license_key",licenseKey.Trim()},{"device_id",InstallationId},{"device_name",Environment.MachineName??applicationName},{"platform",Environment.OSVersion.Platform.ToString().ToLowerInvariant()},{"app_version",applicationVersion}};if(!string.IsNullOrWhiteSpace(email))body["email"]=email.Trim();
        try{body["fingerprint"]=fingerprintProvider(product);}catch{return Error("HARDWARE_UNAVAILABLE","Device information is unavailable. Contact the software publisher.","local");}
        ApiResponse response;try{response=await RequestAsync(HttpMethod.Post,"/api/v1/licenses/activate",body,new(){{"X-App-ID",product},{"X-Environment",environment}},cancellationToken);}catch{return Error("NETWORK_ERROR","Activation requires an internet connection.","local");}
        if(!response.Ok)return ResponseError(response);
        if(!response.Body.TryGetProperty("activation_token",out var tokenElement)||tokenElement.GetString() is not string token||!token.StartsWith("act_")||!response.Body.TryGetProperty("license",out var signed))return Error("INVALID_RESPONSE","The activation response was incomplete.","online",response);
        LicenseResult result;try{result=await VerifiedAsync(signed,"online",response,cancellationToken);}catch{await BestEffortDeactivate(token);return Error("SIGNATURE_INVALID","The license could not be verified.","online",response);}
        if(!result.Valid&&result.ErrorCode is not ("SESSION_REQUIRED" or "INSUFFICIENT_CREDITS")){await BestEffortDeactivate(token);return result;}AtomicWrite("activation-token",token);await SaveState(signed,cancellationToken);return result.ErrorCode=="SESSION_REQUIRED"?await SeatInnerAsync("checkout",cancellationToken):result;
    }

    public Task<LicenseResult> CheckAsync(bool forceOnline=false,CancellationToken cancellationToken=default)=>SynchronizedAsync(()=>CheckInnerAsync(forceOnline,cancellationToken),cancellationToken);
    private async Task<LicenseResult> CheckInnerAsync(bool forceOnline,CancellationToken cancellationToken)
    {
        var token=ReadText("activation-token");if(token is null)return Error("NOT_ACTIVATED","No activation is stored for this installation.","local");
        JsonElement fingerprint;try{fingerprint=fingerprintProvider(product);}catch{return Error("HARDWARE_UNAVAILABLE","Device information is unavailable.","local");}
        var state=ReadJson("signed-state.json");if(!forceOnline&&state.ValueKind==JsonValueKind.Object&&state.TryGetProperty("signed_license",out var cachedSigned)){try{var cached=await VerifiedAsync(cachedSigned,"cache",null,cancellationToken);var next=Time(cached.Payload,"next_validation_at");if(cached.Valid&&next is not null&&clock()<next){Checkpoint();if(cached.Payload.GetProperty("floating").ValueKind==JsonValueKind.Object)StartHeartbeat(cached.Payload.GetProperty("floating"));return cached;}}catch{Remove("signed-state.json");}}
        ApiResponse response;try{response=await RequestAsync(HttpMethod.Post,"/api/v1/licenses/validate",new{fingerprint,session_token=SessionToken()},AuthHeaders(token),cancellationToken);}catch{return await OfflineAsync(cancellationToken);}
        if(!response.Ok){var result=ResponseError(response);
            if(response.Status==402&&result.ErrorCode=="INSUFFICIENT_CREDITS"){
                try{var emptySigned=response.Body.GetProperty("license");var verified=await VerifiedAsync(emptySigned,"online",response,cancellationToken);if(verified.SignatureValid&&verified.ErrorCode=="INSUFFICIENT_CREDITS"){await SaveState(emptySigned,cancellationToken);return verified;}}catch{}
                return Error("SIGNATURE_INVALID","The credit balance could not be verified.","online",response);
            }
            StopHeartbeat();Remove("floating-session.json");if(result.ErrorCode=="SESSION_NOT_FOUND")return await SeatInnerAsync("checkout",cancellationToken);
            Remove("signed-state.json");if(new[]{"INVALID_TOKEN","DEVICE_MISMATCH","FINGERPRINT_MISMATCH","LICENSE_REVOKED","LICENSE_EXPIRED","LICENSE_CANCELLED","LICENSE_SUSPENDED"}.Contains(result.ErrorCode))Remove("activation-token");return result;}
        if(!response.Body.TryGetProperty("license",out var signed)){Remove("signed-state.json");return Error("INVALID_RESPONSE","The validation response was incomplete.","online",response);}
        try{var result=await VerifiedAsync(signed,"online",response,cancellationToken);if(result.Valid){await SaveState(signed,cancellationToken);if(result.Payload.GetProperty("floating").ValueKind==JsonValueKind.Object)StartHeartbeat(result.Payload.GetProperty("floating"));}else Remove("signed-state.json");return result;}catch{Remove("signed-state.json");return Error("SIGNATURE_INVALID","The license could not be verified.","online",response);}
    }

    public Task<LicenseResult> ValidateAsync(CancellationToken cancellationToken=default)=>CheckAsync(true,cancellationToken);
    public Task<LicenseResult> DeactivateAsync(CancellationToken cancellationToken=default)=>SynchronizedAsync(()=>DeactivateInnerAsync(cancellationToken),cancellationToken);
    private async Task<LicenseResult> DeactivateInnerAsync(CancellationToken cancellationToken){StopHeartbeat();Remove("floating-session.json");var token=ReadText("activation-token");if(token is null)return Error("NOT_ACTIVATED","No activation is stored for this installation.","local");ApiResponse response;try{response=await RequestAsync(HttpMethod.Post,"/api/v1/licenses/deactivate",new{},new(){{"Authorization","Bearer "+token}},cancellationToken);}catch{return Error("NETWORK_ERROR","Deactivation requires an internet connection.","local");}if(!response.Ok)return ResponseError(response);if(!response.Body.TryGetProperty("deactivated",out var released)||released.ValueKind!=JsonValueKind.True)return Error("INVALID_RESPONSE","Deactivation was not confirmed.","online",response);Remove("activation-token");Remove("signed-state.json");return new(true,"online",EmptyPayload(),Message:"License deactivated.",RequestId:RequestId(response));}

    private async Task<LicenseResult> OfflineAsync(CancellationToken cancellationToken){var state=ReadJson("signed-state.json");if(state.ValueKind!=JsonValueKind.Object||!state.TryGetProperty("signed_license",out var signed))return Error("OFFLINE_CACHE_MISSING","Online validation is required because no verified cache is available.","offline");try{var result=await VerifiedAsync(signed,"offline",null,cancellationToken);if(result.Valid)Checkpoint();return result;}catch{Remove("signed-state.json");return Error("OFFLINE_CACHE_INVALID","Connect to verify your license again.","offline");}}

    private async Task<LicenseResult> VerifiedAsync(JsonElement signed,string source,ApiResponse? response,CancellationToken cancellationToken)
    {
        if(!signed.TryGetProperty("payload",out var payload)||!signed.TryGetProperty("signature",out var signature)||signed.GetProperty("algorithm").GetString()!="Ed25519")throw new InvalidDataException("The signed license envelope is incomplete.");
        var key=await GetVerificationKey(signed,cancellationToken);if(signed.GetProperty("key_id").GetString()!=key["key_id"])throw new InvalidDataException("The license signature key does not match the trusted key.");
        string? encoded=null;
        if(signed.TryGetProperty("payload_json",out var raw)){
            if(raw.ValueKind!=JsonValueKind.String||Encoding.UTF8.GetByteCount(raw.GetString()!)>1048576)throw new InvalidDataException("Invalid signed bytes.");
            encoded=raw.GetString()!;using var parsed=JsonDocument.Parse(encoded);payload=parsed.RootElement.Clone();
        }
        if(payload.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid signed payload.");
        var verifier=new Ed25519Signer();verifier.Init(false,new Ed25519PublicKeyParameters(Decode(key["public_key"]),0));var canonical=Encoding.UTF8.GetBytes(encoded??Canonical(payload));verifier.BlockUpdate(canonical,0,canonical.Length);if(!verifier.VerifySignature(Decode(signature.GetString()!)))throw new CryptographicException("The license signature is invalid.");
        if(payload.GetProperty("product_id").GetString()!=product||payload.GetProperty("environment").GetString()?.ToUpperInvariant()!=environment)throw new CryptographicException("The signed license belongs to another product or environment.");
        var expected=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(InstallationId))).ToLowerInvariant();if(payload.GetProperty("device").GetProperty("installation_hash").GetString()!=expected)throw new CryptographicException("The signed license belongs to another installation.");
        if(!payload.GetProperty("device").TryGetProperty("fingerprint",out var binding)||!HardwareFingerprint.Matches(binding,fingerprintProvider(product)))return Error("FINGERPRINT_MISMATCH","This activation belongs to a different device.",source,response,payload,true);
        var now=clock();var state=ReadJson("signed-state.json");var checkpoint=Time(state,"last_seen_at");var server=Time(payload,"server_time");var latest=new[]{checkpoint,server}.Where(x=>x is not null).Max();
        var next=Time(payload,"next_validation_at");var signedGrace=Time(payload,"offline_grace_until");
        if(server is null||next is null||signedGrace is null||next<server||signedGrace<server||!payload.TryGetProperty("expires_at",out var expiration)||(expiration.ValueKind!=JsonValueKind.Null&&Time(payload,"expires_at") is null))return Error("INVALID_SIGNED_TIME","The license dates could not be verified.",source,response,payload,true);
        if(latest is not null&&now.AddMinutes(5)<latest)return Error("CLOCK_ROLLBACK_DETECTED","The system clock moved behind the last trusted license time.",source,response,payload,true);
        if(payload.GetProperty("status").GetString()?.ToLowerInvariant()!="active")return Error("LICENSE_NOT_ACTIVE","The license is not active.",source,response,payload,true);
        var expires=Time(payload,"expires_at");if(expires is not null&&now>=expires)return Error("LICENSE_EXPIRED","The license has expired.",source,response,payload,true);
        var grace=Time(payload,"offline_grace_until");if(source is "cache" or "offline"&&(grace is null||now>grace))return Error("OFFLINE_GRACE_EXPIRED","The offline grace period ended; connect to validate the license.",source,response,payload,true);
        if(!payload.TryGetProperty("floating",out var floating)||(floating.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)))return Error("INVALID_RESPONSE","The shared-seat policy is unavailable.",source,response,payload,true);
        if(floating.ValueKind==JsonValueKind.Object){if(!floating.TryGetProperty("heartbeat_interval_seconds",out var interval)||!interval.TryGetInt32(out var seconds)||seconds is <30 or >600||!floating.TryGetProperty("max_concurrent_sessions",out var max)||!max.TryGetInt32(out var seats)||seats<1)return Error("INVALID_RESPONSE","The shared-seat settings are unavailable.",source,response,payload,true);if(!floating.TryGetProperty("session",out var session)||session.ValueKind==JsonValueKind.Null)return Error("SESSION_REQUIRED","Reserve a shared seat before using this app.",source,response,payload,true);}
        if(!LicensePolicy.Lease(payload,SessionToken(),now))return Error("SESSION_EXPIRED","Your shared seat expired. Connect to reserve a seat again.",source,response,payload,true);
        if(!LicensePolicy.Credits(payload))return Error("INVALID_RESPONSE","The credit policy is unavailable.",source,response,payload,true);
        if(payload.GetProperty("metered").GetBoolean()&&source!="online")return Error("METERED_ONLINE_REQUIRED","Connect to check or use credits.",source,response,payload,true);
        if(payload.GetProperty("metered").GetBoolean()&&payload.GetProperty("credit_balance").GetInt64()==0)return Error("INSUFFICIENT_CREDITS","There are not enough credits. Contact the software publisher.",source,response,payload,true);
        return new(true,source,payload.Clone(),RequestId:response is null?null:RequestId(response),SignatureValid:true,Clock:clock,SeatGuard:p=>LicensePolicy.Lease(p,SessionToken(),clock()));
    }

    private async Task<Dictionary<string,string>> GetVerificationKey(JsonElement signed,CancellationToken cancellationToken)
    {
        if(verificationKey is not null)return verificationKey;var state=ReadJson("signed-state.json");if(state.ValueKind==JsonValueKind.Object&&state.TryGetProperty("verification_key",out var cached)&&cached.TryGetProperty("key_id",out var id)&&id.GetString()==signed.GetProperty("key_id").GetString())return new(){{"key_id",id.GetString()!},{"public_key",cached.GetProperty("public_key").GetString()!}};
        var response=await RequestAsync(HttpMethod.Get,"/api/v1/public-key",null,new(),cancellationToken);if(!response.Ok||!response.Body.TryGetProperty("key",out var key)||key.GetProperty("key_id").GetString()!=signed.GetProperty("key_id").GetString()||key.GetProperty("algorithm").GetString()!="Ed25519")throw new CryptographicException("The server verification key could not authenticate this license.");verificationKey=new(){{"key_id",key.GetProperty("key_id").GetString()!},{"public_key",key.GetProperty("public_key").GetString()!}};return verificationKey;
    }

    private async Task SaveState(JsonElement signed,CancellationToken cancellationToken){var key=await GetVerificationKey(signed,cancellationToken);WriteJson("signed-state.json",new{version=1,signed_license=signed,verification_key=new{key_id=key["key_id"],public_key=key["public_key"]},last_seen_at=clock().ToString("O")});}
    private void Checkpoint(){var state=ReadJson("signed-state.json");var map=state.ValueKind==JsonValueKind.Object?JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(state.GetRawText())??new():new();map["last_seen_at"]=JsonSerializer.SerializeToElement(clock().ToString("O"));WriteJson("signed-state.json",map);}
    private async Task BestEffortDeactivate(string token){try{await RequestAsync(HttpMethod.Post,"/api/v1/licenses/deactivate",new{},new(){{"Authorization","Bearer "+token}},CancellationToken.None);}catch{}}

    private async Task<ApiResponse> RequestAsync(HttpMethod method,string endpoint,object? body,Dictionary<string,string> headers,CancellationToken cancellationToken){using var request=new HttpRequestMessage(method,baseUrl+endpoint);request.Headers.Accept.ParseAdd("application/json");request.Headers.UserAgent.ParseAdd("KDPika-CSharp-SDK/1.0.1");request.Headers.TryAddWithoutValidation("X-Installation-ID",InstallationId);foreach(var pair in headers){request.Headers.Remove(pair.Key);request.Headers.TryAddWithoutValidation(pair.Key,pair.Value);}if(body is not null)request.Content=JsonContent.Create(body);using var response=await http.SendAsync(request,cancellationToken);var json=await response.Content.ReadAsStringAsync(cancellationToken);JsonElement parsed;try{parsed=JsonDocument.Parse(json).RootElement.Clone();if(parsed.ValueKind!=JsonValueKind.Object)throw new JsonException();}catch{parsed=JsonSerializer.SerializeToElement(new{success=false,error=new{code="INVALID_RESPONSE",message="The server returned an invalid response."}});}return new(response.IsSuccessStatusCode&&parsed.TryGetProperty("success",out var success)&&success.ValueKind==JsonValueKind.True,(int)response.StatusCode,parsed);}
    private LicenseResult ResponseError(ApiResponse response)=>Error(response.Body.TryGetProperty("error",out var error)&&error.TryGetProperty("code",out var code)?code.GetString()!:"HTTP_ERROR",error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("message",out var message)?message.GetString()!:"The licensing service rejected the request.","online",response);
    private LicenseResult Error(string code,string message,string source,ApiResponse? response=null,JsonElement payload=default,bool signature=false)=>new(false,source,payload.ValueKind==JsonValueKind.Undefined?EmptyPayload():payload.Clone(),code,message,response is null?null:RequestId(response),signature);
    private static string? RequestId(ApiResponse response)=>response.Body.TryGetProperty("request_id",out var id)?id.GetString():null;
    private static DateTimeOffset? Time(JsonElement element,string name)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&DateTimeOffset.TryParse(value.GetString(),out var parsed)?parsed:null;
    private JsonElement ReadJson(string name){try{return JsonDocument.Parse(File.ReadAllText(Path.Combine(storageDir,name))).RootElement.Clone();}catch{return default;}}
    private string? ReadText(string name){try{var value=File.ReadAllText(Path.Combine(storageDir,name)).Trim();return value.Length>0?value:null;}catch{return null;}}
    private void WriteJson(string name,object value)=>AtomicWrite(name,JsonSerializer.Serialize(value));
    private void AtomicWrite(string name,string value){var target=Path.Combine(storageDir,name);var temporary=target+"."+Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();File.WriteAllText(temporary,value);TryUnixMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(temporary,target,true);TryUnixMode(target,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
    private void Remove(string name){try{File.Delete(Path.Combine(storageDir,name));}catch{}}
    private static byte[] Decode(string value)=>Convert.FromBase64String(value.Replace('-','+').Replace('_','/')+new string('=',(4-value.Length%4)%4));
    private static string Canonical(JsonElement value)=>value.ValueKind switch{JsonValueKind.Object=>"{"+string.Join(",",value.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal).Select(x=>JsonSerializer.Serialize(x.Name)+":"+Canonical(x.Value)))+"}",JsonValueKind.Array=>"["+string.Join(",",value.EnumerateArray().Select(Canonical))+"]",_=>value.GetRawText()};
    private static JsonElement EmptyPayload()=>JsonDocument.Parse("{}").RootElement.Clone();
    private static bool SafeBaseUrl(string value)=>Uri.TryCreate(value.Trim(),UriKind.Absolute,out var uri)&&string.IsNullOrEmpty(uri.UserInfo)&&(uri.Scheme==Uri.UriSchemeHttps||(uri.Scheme==Uri.UriSchemeHttp&&(uri.IsLoopback||uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase))));
    private static string DefaultStorageDir(string name){var safe=new string(name.Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_'?c:'-').ToArray()).Trim('-');if(string.IsNullOrEmpty(safe))safe="licensed-app";if(OperatingSystem.IsMacOS())return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),safe,"License");if(OperatingSystem.IsWindows())return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),safe,"License");var root=Environment.GetEnvironmentVariable("XDG_DATA_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","share");return Path.Combine(root,safe,"license");}
    private static void TryUnixMode(string path,UnixFileMode mode){try{if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,mode);}catch{}}
    private sealed record ApiResponse(bool Ok,int Status,JsonElement Body);
}
