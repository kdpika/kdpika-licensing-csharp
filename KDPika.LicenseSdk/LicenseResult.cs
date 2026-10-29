using System.Text.Json;

namespace KDPika.LicenseSdk;

public sealed record LicenseResult(bool Valid, string Source, JsonElement Payload, string? ErrorCode = null, string Message = "", string? RequestId = null, bool SignatureValid = false, string StorageSecurity = "permission-protected-file", [property: System.Text.Json.Serialization.JsonIgnore] Func<DateTimeOffset>? Clock = null, [property: System.Text.Json.Serialization.JsonIgnore] Func<JsonElement,bool>? SeatGuard = null)
{
    private bool Allowed()
    {
        if(!Valid||!SignatureValid||Payload.ValueKind!=JsonValueKind.Object)return false;
        if(!Payload.TryGetProperty("status",out var status)||status.GetString()!="active")return false;
        DateTimeOffset? Time(string name)=>Payload.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String&&DateTimeOffset.TryParse(v.GetString(),out var t)?t:null;
        var now=(Clock??(()=>DateTimeOffset.UtcNow))();var server=Time("server_time");var next=Time("next_validation_at");var grace=Time("offline_grace_until");
        if(server is null||next is null||grace is null||next<server||grace<server||now.AddMinutes(5)<server)return false;
        if(!Payload.TryGetProperty("expires_at",out var expiry)||(expiry.ValueKind!=JsonValueKind.Null&&(Time("expires_at") is not DateTimeOffset end||now>=end)))return false;
        if(!LicensePolicy.Credits(Payload))return false;
        if(Payload.GetProperty("metered").GetBoolean()&&Source!="online")return false;
        if(!Payload.TryGetProperty("floating",out var floating))return false;
        if(floating.ValueKind!=JsonValueKind.Null&&(SeatGuard is null||!SeatGuard(Payload)))return false;
        return Source is "cache" or "offline" ? now<=grace : now<next;
    }
    public bool Feature(string name, bool fallback = false)
    {
        if(!Allowed()||!Payload.TryGetProperty("features",out var features)||features.ValueKind!=JsonValueKind.Object)return false;
        if(!features.TryGetProperty(name,out var value))return fallback;
        return value.ValueKind==JsonValueKind.True||(value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number)&&double.IsFinite(number)&&number>0);
    }
    public JsonElement? FeatureValue(string name)=>Allowed()&&Payload.TryGetProperty("features",out var features)&&features.ValueKind==JsonValueKind.Object&&features.TryGetProperty(name,out var value)?value.Clone():null;
}
