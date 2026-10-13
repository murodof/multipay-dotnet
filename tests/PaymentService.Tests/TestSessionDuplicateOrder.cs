using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Infrastructure.Repositories;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Step (d) and idempotency: one order_id, one session — ever, and also under concurrency.
[Collection("app")]
public sealed class TestSessionDuplicateOrder(Containers containers) : AppTest(containers)
{
    [Fact]
    public async Task A_second_hash_for_the_same_order_is_refused()
    {
        var first = await CreateSession(Client, MakeHash(("order_id", "order-1"), ("amount", 5_000_000)));
        var second = await CreateSession(Client, MakeHash(("order_id", "order-1"), ("amount", 100_000)));

        Assert.True((await first.Json())["success"].IsTrue());
        Assert.Equal(409, (int)second.StatusCode);
        Assert.Equal("ORDER_EXISTS", await ErrorOf(second));
        Assert.Equal(-2004, (await second.Json())["error"]!["error_code"]!.GetValue<int>());

        var rows = await App.StoredSessions();
        Assert.Single(rows);
        Assert.Equal(5_000_000, rows[0].Amount);
    }

    // Completed, failed and expired sessions all keep the order: a paid order must never be paid twice.
    [Fact]
    public async Task A_finished_session_still_blocks_its_order()
    {
        await CreateSession(Client, MakeHash(("order_id", "order-1")));
        await using (var db = App.Db())
        {
            await db.PaymentSessions.ExecuteUpdateAsync(setter => setter.SetProperty(session => session.Status, PaymentSessionStatus.SUCCESS));
        }

        Assert.Equal("ORDER_EXISTS", await ErrorOf(await CreateSession(Client, MakeHash(("order_id", "order-1")))));
    }

    [Fact]
    public async Task Concurrent_requests_for_one_order_open_exactly_one_session()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            CreateSession(ClientFrom($"198.51.100.{i}"), MakeHash(("order_id", "order-race"), ("phone", $"99890123456{i}")))));

        var outcomes = new List<string>();
        foreach (var response in responses)
        {
            outcomes.Add((await response.Json())["success"].IsTrue() ? "ok" : await ErrorOf(response));
        }
        outcomes.Sort(StringComparer.Ordinal);
        Assert.Equal(["ORDER_EXISTS", "ORDER_EXISTS", "ORDER_EXISTS", "ORDER_EXISTS", "ok"], outcomes);
        Assert.Single(await App.StoredSessions());
    }

    // The lookup and the insert are two statements, so two requests can both see "no session".
    // The unique constraint on order_id is the actual guarantee; the lookup only saves an INSERT.
    [Fact]
    public async Task The_unique_constraint_catches_what_the_lookup_misses()
    {
        App.Overrides.Add(services => services.AddScoped<PaymentSessionRepository, LookupMisses>());
        await CreateSession(Client, MakeHash(("order_id", "order-1")));

        var response = await CreateSession(Client, MakeHash(("order_id", "order-1")));

        Assert.Equal(409, (int)response.StatusCode);
        Assert.Equal("ORDER_EXISTS", await ErrorOf(response));
        Assert.Single(await App.StoredSessions());
    }

    [Fact]
    public async Task The_order_is_checked_before_phone_and_amount()
    {
        await CreateSession(Client, MakeHash(("order_id", "order-1")));

        var response = await CreateSession(Client, MakeHash(("order_id", "order-1"), ("phone", "bad"), ("amount", 1)));

        Assert.Equal("ORDER_EXISTS", await ErrorOf(response));
    }

    // Postgres compares order_id byte-wise; SQL Server's default collation would call "ORDER-1" a duplicate of "order-1".
    [Fact]
    public async Task Order_ids_differing_only_in_case_are_different_orders()
    {
        var lower = await CreateSession(Client, MakeHash(("order_id", "order-1")));
        var upper = await CreateSession(Client, MakeHash(("order_id", "ORDER-1")));

        Assert.True((await lower.Json())["success"].IsTrue());
        Assert.True((await upper.Json())["success"].IsTrue());
    }

    private sealed class LookupMisses(PaymentDbContext session) : PaymentSessionRepository(session)
    {
        public override Task<PaymentSession?> GetByOrderId(string orderId) => Task.FromResult<PaymentSession?>(null);
    }
}
