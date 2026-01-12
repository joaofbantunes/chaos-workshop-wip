using System.Diagnostics;
using Spectre.Console;

namespace ChaosWorkshop.Client;

public enum TestBattery
{
    Single,
    Half,
    Complete
}

public sealed class TestExecutor(OrderClient orderClient, LoyaltyClient loyaltyClient, TimeProvider timeProvider)
{
    public async Task ExecuteAsync(TestBattery battery, CancellationToken ct)
    {
        using var activity = StartActivity(
            "execute test battery",
            ActivityKind.Internal,
            tags:
            [
                new("test.battery", battery.ToString())
            ]);

        AnsiConsole.MarkupLine(
            $"[orange1]If you want to check Grafana, the trace id for this run is: [bold]{activity?.TraceId}[/][/]");

        await ResetServicesAsync(ct);

        try
        {
            await RunTestsAsync(TestBatteryDefinition.Get(battery), ct);
            AnsiConsole.WriteLine("Test execution completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine("⚠️ Test execution cancelled");
        }
        catch (HttpRequestException)
        {
            AnsiConsole.WriteLine("❌ Too many failed requests, aborting test execution");
        }
        catch (Exception e)
        {
            AnsiConsole.WriteLine($"❌ Unexpected test execution error: {e.Message}");
        }
    }

    private async Task ResetServicesAsync(CancellationToken ct)
    {
        using var activity = StartActivity("reset services db", ActivityKind.Internal);

        await AnsiConsole.Status().StartAsync(
            "Resetting services before running tests...",
            async _ =>
            {
                await orderClient.ResetDbAsync(ct);
                await loyaltyClient.ResetDbAsync(ct);
            });
    }

    private async Task RunTestsAsync(IEnumerable<TestDefinition> tests, CancellationToken ct)
    {
        using var activity = StartActivity("run tests", ActivityKind.Internal);

        foreach (var (test, index) in tests.Select((value, index) => (value, index)))
        {
            AnsiConsole.Write(new Rule($"Running test {index + 1}")
            {
                Justification = Justify.Left,
                Style = new Style(foreground: Color.Blue)
            });
            var startTimestamp = timeProvider.GetTimestamp();
            if (await RunTestAsync(test, index, ct))
            {
                AnsiConsole.Write(
                    new Rule($"✅ Test {index + 1} passed (took {GetElapsedTimeString(timeProvider, startTimestamp)})")
                    {
                        Justification = Justify.Left,
                        Style = new Style(foreground: Color.Green)
                    });
            }
            else
            {
                AnsiConsole.Write(
                    new Rule($"❌ Test {index + 1} failed (took {GetElapsedTimeString(timeProvider, startTimestamp)})")
                    {
                        Justification = Justify.Left,
                        Style = new Style(foreground: Color.Red)
                    });
            }
        }

        static string GetElapsedTimeString(TimeProvider timeProvider, long startTimestamp)
        {
            var elapsed = timeProvider.GetElapsedTime(startTimestamp);
            return elapsed switch
            {
                { Days: > 0 } => $"{elapsed.Days}d {elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s",
                { Hours: > 0 } => $"{elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s",
                { Minutes: > 0 } => $"{elapsed.Minutes}m {elapsed.Seconds}s",
                _ => $"{elapsed.Seconds}s"
            };
        }
    }

    private async Task<bool> RunTestAsync(TestDefinition test, int index, CancellationToken ct)
    {
        using var activity = StartActivity(
            "run test",
            ActivityKind.Internal,
            tags:
            [
                new("test.index", index)
            ]);

        Guid orderId;

        using (var _ = StartActivity("place order", ActivityKind.Internal))
        {
            try
            {
                var result = await AnsiConsole.Status().StartAsync(
                    "Placing order...",
                    _ => orderClient.PlaceOrderAsync(test.PlaceOrderRequest, ct));
                orderId = result.OrderId;
            }
            catch (HttpRequestException)
            {
                AnsiConsole.WriteLine("❌ Too many failed requests, aborting test execution");
                return false;
            }
        }

        using (var _ = StartActivity("assert outcome", ActivityKind.Internal))
        {
            if (orderId != Guid.Empty)
            {
                AnsiConsole.WriteLine($"✅ Order placed with id: {orderId}");
            }
            else
            {
                AnsiConsole.WriteLine("❌ Order placement failed");
                return false;
            }

            var orders = await AnsiConsole.Status().StartAsync(
                "Fetching orders...",
                _ =>
                {
                    using var orderListActivity = StartActivity("list orders", ActivityKind.Internal);
                    return orderClient.ListOrdersAsync(test.PlaceOrderRequest.CustomerId, ct);
                });

            var placedOrder = orders.FirstOrDefault(o => o.Id == orderId);

            if (placedOrder != null)
            {
                AnsiConsole.WriteLine("✅ Order found in list with id");
            }
            else
            {
                AnsiConsole.WriteLine("❌ Order not found in list");
                return false;
            }

            var areOrderDetailsCorrect = true;

            if (placedOrder.DiscountPercentage == test.ExpectedPlacedOrderDetails.DiscountPercentage)
            {
                AnsiConsole.WriteLine($"✅ Discount percentage is correct ({placedOrder.DiscountPercentage})");
            }
            else
            {
                areOrderDetailsCorrect = false;
                AnsiConsole.WriteLine(
                    $"❌ Discount percentage is incorrect (expected {test.ExpectedPlacedOrderDetails.DiscountPercentage}, got {placedOrder.DiscountPercentage})");
            }

            if (placedOrder.Amount == test.ExpectedPlacedOrderDetails.Amount)
            {
                AnsiConsole.WriteLine($"✅ Amount is correct ({placedOrder.Amount})");
            }
            else
            {
                areOrderDetailsCorrect = false;
                AnsiConsole.WriteLine(
                    $"❌ Amount is incorrect (expected {test.ExpectedPlacedOrderDetails.Amount}, got {placedOrder.Amount})");
            }

            if (!areOrderDetailsCorrect) return false;

            if (orders.Count == test.ExpectedCustomerOrdersCount)
            {
                AnsiConsole.WriteLine($"✅ Customer orders count is correct ({orders.Count})");
            }
            else
            {
                AnsiConsole.WriteLine(
                    $"❌ Customer orders count is incorrect (expected {test.ExpectedCustomerOrdersCount}, got {orders.Count})");
                return false;
            }

            var nextLoyaltyDiscount = await AnsiConsole.Status().StartAsync(
                "Fetching loyalty discount...",
                _ =>
                {
                    using var loyaltyDiscountActivity = StartActivity("get customer discount", ActivityKind.Internal);
                    return loyaltyClient.GetDiscountAsync(test.GetCustomerDiscountRequest.CustomerId, ct);
                });

            if (nextLoyaltyDiscount.DiscountPercentage == test.ExpectedGetCustomerDiscountResponse.DiscountPercentage)
            {
                AnsiConsole.WriteLine(
                    $"✅ Loyalty discount percentage is correct ({nextLoyaltyDiscount.DiscountPercentage})");
            }
            else
            {
                AnsiConsole.WriteLine(
                    $"❌ Loyalty discount percentage is incorrect (expected {test.ExpectedGetCustomerDiscountResponse.DiscountPercentage}, got {nextLoyaltyDiscount.DiscountPercentage})");
                return false;
            }
        }

        return true;
    }
}