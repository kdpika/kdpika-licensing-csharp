// Sandbox session CLI, .NET 10 LTS and the matching SDK source beside Session.csproj.
// Run dotnet run --project Session.csproj -- status|activate|check|validate|deactivate.
// Set KDP_APP_ID,KDP_STATE_DIR; activate: KDP_LICENSE_KEY/optional customer email.
// Expected safe JSON decisions, not a token/raw payload. Keep the Valid guard.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KDPika.LicenseSdk;

static string Required(string name){var value=Environment.GetEnvironmentVariable(name)?.Trim();return string.IsNullOrEmpty(value)?throw new ArgumentException("Missing configuration"):value;}
try{
    var action=args.Length==1?args[0]:"";
    if(!new[]{"status","activate","check","validate","deactivate"}.Contains(action))throw new ArgumentException("Choose an action");
    var origin=(Environment.GetEnvironmentVariable("KDP_BASE_URL")??"https://licensing.kdpika.com").TrimEnd('/');
    var uri=new Uri(origin);var local=uri.Scheme=="http"&&new[]{"127.0.0.1","localhost"}.Contains(uri.Host)&&Environment.GetEnvironmentVariable("KDP_ALLOW_LOOPBACK")=="1";
    if(!string.IsNullOrEmpty(uri.UserInfo)||uri.AbsolutePath!="/"||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment)||(uri.Scheme!="https"&&!local))throw new ArgumentException("Use an HTTPS site origin");
    var client=new LicenseClient(Required("KDP_APP_ID"),"SANDBOX",origin,applicationName:"KDPika SDK session example",
        applicationVersion:"1.0.0",storageDir:Required("KDP_STATE_DIR"));
    if(action=="status"){
        Console.WriteLine(JsonSerializer.Serialize(new{activated=client.Activated,storage_security="permission-protected-file",
            installation_hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.InstallationId))).ToLowerInvariant()}));return;
    }
    LicenseResult result;
    if(action=="activate"){
        var key=Required("KDP_LICENSE_KEY");if(!Regex.IsMatch(key,"^TEST[A-Z0-9]*-",RegexOptions.IgnoreCase))throw new ArgumentException("Use a Sandbox key");
        result=await client.ActivateAsync(key,Environment.GetEnvironmentVariable("KDP_CUSTOMER_EMAIL"));
    }else result=action switch{"check"=>await client.CheckAsync(),"validate"=>await client.ValidateAsync(),_=>await client.DeactivateAsync()};
    var active=action!="deactivate"&&result.Valid&&result.SignatureValid;
    var feature=active?result.FeatureValue("seat_count"):null;
    int? limit=feature is JsonElement element&&element.ValueKind==JsonValueKind.Number&&element.TryGetInt32(out var number)&&number>=0?number:null;
    Console.WriteLine(JsonSerializer.Serialize(new{valid=result.Valid,source=result.Source,error_code=result.ErrorCode,
        signature_valid=result.SignatureValid,pdf_export_allowed=active&&result.Feature("pdf_export"),seat_count=limit,
        request_id=Regex.IsMatch(result.RequestId??"","^req_[a-f0-9]{24}$")?result.RequestId:null,
        message=action=="deactivate"&&result.Valid?"This installation is deactivated.":result.Valid?"Your license is ready.":"Your license could not be confirmed. Check your key or contact the software publisher."}));
    Environment.ExitCode=result.Valid?0:1;
}catch{
    Console.Error.WriteLine("License check unavailable. Review configuration, connectivity and private storage permissions. Do not disable signature verification.");Environment.ExitCode=2;
}
