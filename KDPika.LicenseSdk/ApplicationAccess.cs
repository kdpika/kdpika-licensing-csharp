using System.Text.Json;
namespace KDPika.LicenseSdk;
public static class ApplicationAccess
{
    public static bool Allowed(JsonElement payload,string type)
    {
        if(type is not ("desktop" or "web")||payload.ValueKind!=JsonValueKind.Object)return false;
        var scope="both";
        if(payload.TryGetProperty("application_access",out var access)){if(access.ValueKind!=JsonValueKind.String)return false;scope=access.GetString()??"";}
        else if(payload.TryGetProperty("features",out var features)&&features.ValueKind==JsonValueKind.Object&&features.TryGetProperty("application_access",out access)){if(access.ValueKind!=JsonValueKind.String)return false;scope=access.GetString()??"";}
        string? bound=null;if(payload.TryGetProperty("application_type",out var context)&&context.ValueKind!=JsonValueKind.Null){if(context.ValueKind!=JsonValueKind.String)return false;bound=context.GetString();}
        return (scope is "both" or "desktop" or "web")&&(scope=="both"||scope==type)&&(bound is null?scope=="both":bound==type);
    }
}
