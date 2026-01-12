using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ChaosWorkshop.Client;

public sealed class LoyaltyClient(HttpClient client)
{
    public async Task<GetCustomerDiscountResponse> GetDiscountAsync(Guid customerId, CancellationToken ct)
    {
        var response = await client.GetAsync($"customer-discounts/{customerId}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GetCustomerDiscountResponse>(
                   LoyaltySerializerContext.Default.GetCustomerDiscountResponse,
                   ct)
               ?? throw new InvalidOperationException("Failed to deserialize GetCustomerDiscountResponse");
    }

    public async Task ResetDbAsync(CancellationToken ct)
    {
        var response = await client.PostAsync("assertions/reset-db", null, ct);
        response.EnsureSuccessStatusCode();
    }
}

[JsonSerializable(typeof(GetCustomerDiscountResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class LoyaltySerializerContext : JsonSerializerContext;

public sealed record GetCustomerDiscountRequest
{
    public required Guid CustomerId { get; init; }
}

public sealed record GetCustomerDiscountResponse
{
    public required decimal DiscountPercentage { get; init; }
}