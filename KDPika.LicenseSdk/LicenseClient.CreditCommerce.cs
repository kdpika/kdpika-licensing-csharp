using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace KDPika.LicenseSdk;

public sealed partial class LicenseClient
{
    public Task<JsonElement> CreditPacksAsync(CancellationToken cancellationToken=default)=>CreditRequestAsync("packs",new(),cancellationToken);
    public Task<JsonElement> CreditHistoryAsync(CancellationToken cancellationToken=default)=>CreditRequestAsync("history",new(),cancellationToken);
    public Task<JsonElement> CreateCreditCheckoutAsync(string packId,CancellationToken cancellationToken=default)=>CreditRequestAsync("checkout",new(){{"pack_id",packId}},cancellationToken);
    public Task<JsonElement> RedeemCreditsAsync(string code,string idempotencyKey,CancellationToken cancellationToken=default)
    {
        if(!Regex.IsMatch(idempotencyKey,@"\A[A-Za-z0-9_.:-]{8,190}\z"))throw new CreditOperationException("INVALID_REQUEST");
        return CreditRequestAsync("redeem",new(){{"code",code},{"idempotency_key",idempotencyKey}},cancellationToken);
    }
    private Task<JsonElement> CreditRequestAsync(string operation,Dictionary<string,object?> fields,CancellationToken cancellationToken)=>SynchronizedAsync(async()=>
    {
        var token=ReadText("activation-token")??throw new CreditOperationException("NOT_ACTIVATED");
        fields["fingerprint"]=fingerprintProvider(product);fields["session_token"]=SessionToken();
        ApiResponse response;try{response=await RequestAsync(HttpMethod.Post,"/api/v1/credits/"+operation,fields,AuthHeaders(token),cancellationToken);}catch{throw new CreditOperationException("NETWORK_ERROR");}
        if(!response.Ok)throw new CreditOperationException(ResponseError(response).ErrorCode??"HTTP_ERROR");
        var signed=response.Body.GetProperty("license");var result=await VerifiedAsync(signed,"online",response,cancellationToken);
        if(!result.Valid||!result.SignatureValid)throw new CreditOperationException(result.ErrorCode??"SIGNATURE_INVALID");
        var data=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(response.Body.GetRawText())!;data.Remove("license");data["balance"]=result.Payload.GetProperty("credit_balance").Clone();
        if(operation=="redeem"){
            var receipt=result.Payload.GetProperty("credit_transaction");
            static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            var expected=Hash("code:"+Hash((string)fields["idempotency_key"]!));
            if(receipt.GetProperty("type").GetString()!="grant"||receipt.GetProperty("idempotency_hash").GetString()!=expected||receipt.GetProperty("id").GetString()!=data["transaction_id"].GetString())throw new CreditOperationException("SIGNATURE_INVALID");
            data["added"]=receipt.GetProperty("amount").Clone();data["replayed"]=receipt.GetProperty("replayed").Clone();
        }
        await SaveState(signed,cancellationToken);return JsonSerializer.SerializeToElement(data);
    },cancellationToken);
}
