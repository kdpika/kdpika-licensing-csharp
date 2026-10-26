using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KDPika.LicenseSdk;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

var root=Path.Combine(Path.GetTempPath(),"kdpika-csharp-sdk-"+Guid.NewGuid().ToString("N"));
var now=DateTimeOffset.Parse("2026-10-10T12:00:00Z");
var handler=new FakeHandler(()=>now);
var client=new LicenseClient("app_test","SANDBOX","https://licenses.example.test","CSharp SDK Test","1.0",storageDir:root,httpClient:new HttpClient(handler),clock:()=>now,fingerprintProvider:p=>HardwareFingerprint.FromComponents(p,new Dictionary<string,string>{{"machine","synthetic-machine"},{"cpu","synthetic-cpu"}}));
var activated=await client.ActivateAsync("TEST-AAAA-BBBB-CCCC","customer@example.test");
Assert(activated.Valid&&activated.SignatureValid&&activated.Feature("pdf_export"),"Activation/signature/features failed.");
var before=handler.Calls;var cached=await client.CheckAsync();Assert(cached.Valid&&cached.Source=="cache"&&handler.Calls==before,"Verified cache was not used.");
now=now.AddHours(25);handler.Mode="network";var offline=await client.CheckAsync();Assert(offline.Valid&&offline.Source=="offline","Offline grace failed.");
now=now.AddDays(8);var expired=await client.ValidateAsync();Assert(!expired.Valid&&expired.ErrorCode=="OFFLINE_GRACE_EXPIRED","Expired grace was accepted.");
handler.Mode="online";now=DateTimeOffset.Parse("2026-10-10T12:00:00Z");var deactivated=await client.DeactivateAsync();Assert(deactivated.Valid&&!client.Activated,"Deactivation failed.");
Console.WriteLine("C# SDK parity: passed");

static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}

sealed class FakeHandler:HttpMessageHandler
{
    private readonly Func<DateTimeOffset> clock;private readonly Ed25519PrivateKeyParameters secret;private readonly string publicKey;public string Mode="online";public int Calls;
    public FakeHandler(Func<DateTimeOffset> clock){this.clock=clock;secret=new Ed25519PrivateKeyParameters(new SecureRandom());publicKey=Base64Url(secret.GeneratePublicKey().GetEncoded());}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        Calls++;var endpoint=request.RequestUri!.AbsolutePath;if(Mode=="network"&&endpoint.EndsWith("/validate"))throw new HttpRequestException("offline");
        object payload;if(endpoint.EndsWith("/public-key"))payload=new{success=true,key=new{algorithm="Ed25519",key_id="ed25519_test",public_key=publicKey}};
        else if(endpoint.EndsWith("/activate")){var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;payload=new{success=true,activation_token="act_test_token",license=Signed(body.GetProperty("device_id").GetString()!),request_id="req_activate"};}
        else if(endpoint.EndsWith("/validate")){var installation=request.Headers.GetValues("X-Installation-ID").Single();payload=new{success=true,license=Signed(installation),request_id="req_validate"};}
        else payload=new{success=true,deactivated=true,request_id="req_deactivate"};
        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")};
    }
    private object Signed(string installation){var now=clock();var payload=new{license_id="lic_test",product_id="app_test",product="Test",plan="pro",status="active",environment="sandbox",expires_at=now.AddDays(30).ToString("O"),features=new{pdf_export=true,seat_count=5},floating=(object?)null,metered=false,credit_balance=0,total_allocated=0,device=new{fingerprint=new{version=1,hardware_id=HardwareFingerprint.FromComponents("app_test",new Dictionary<string,string>{{"machine","synthetic-machine"},{"cpu","synthetic-cpu"}}).GetProperty("hardware_id").GetString(),components=HardwareFingerprint.FromComponents("app_test",new Dictionary<string,string>{{"machine","synthetic-machine"},{"cpu","synthetic-cpu"}}).GetProperty("components"),policy=new{mode="strict",min_matches=2}},id="dev_test",installation_hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation))).ToLowerInvariant(),name="Test",platform="test",app_version="1.0"},server_time=now.ToString("O"),next_validation_at=now.AddHours(24).ToString("O"),offline_grace_until=now.AddDays(7).ToString("O")};var element=JsonSerializer.SerializeToElement(payload);var canonical=Encoding.UTF8.GetBytes(Canonical(element));var signer=new Ed25519Signer();signer.Init(true,secret);signer.BlockUpdate(canonical,0,canonical.Length);return new{payload,signature=Base64Url(signer.GenerateSignature()),algorithm="Ed25519",key_id="ed25519_test"};}
    private static string Canonical(JsonElement value)=>value.ValueKind switch{JsonValueKind.Object=>"{"+string.Join(",",value.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal).Select(x=>JsonSerializer.Serialize(x.Name)+":"+Canonical(x.Value)))+"}",JsonValueKind.Array=>"["+string.Join(",",value.EnumerateArray().Select(Canonical))+"]",_=>value.GetRawText()};
    private static string Base64Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
}
