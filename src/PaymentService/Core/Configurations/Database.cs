using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Core.Configurations;

public abstract class Base
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options, TimeProvider clock) : DbContext(options)
{
    public const string Collation = "Latin1_General_100_BIN2";

    public DbSet<PaymentSession> PaymentSessions => Set<PaymentSession>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<SvgateLog> SvgateLogs => Set<SvgateLog>();

    public DbSet<HumoLog> HumoLogs => Set<HumoLog>();

    public static string SnakeCase(string name)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]) && index > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(name[index]));
        }
        return builder.ToString();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<string>().UseCollation(Collation);
        configurationBuilder.Properties<DateTimeOffset>().HavePrecision(6);
        configurationBuilder.Properties<DateTimeOffset?>().HavePrecision(6);
        configurationBuilder.Properties<JsonObject>()
            .HaveConversion<JsonObjectConverter, JsonNodeComparer<JsonObject>>()
            .HaveColumnType("nvarchar(max)");
        configurationBuilder.Properties<JsonNode>()
            .HaveConversion<JsonNodeConverter, JsonNodeComparer<JsonNode>>()
            .HaveColumnType("nvarchar(max)");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentSession>(entity =>
        {
            entity.ToTable("payment_sessions", table =>
            {
                table.HasCheckConstraint("ck_payment_sessions_amount_positive", "amount > 0");
                table.HasCheckConstraint("ck_payment_sessions_commission_non_negative", "commission >= 0");
                table.HasCheckConstraint("ck_payment_sessions_total_consistent", "total = amount + commission");
                table.HasCheckConstraint("payment_session_status", StatusCheck<PaymentSessionStatus>());
            });
            entity.HasIndex(session => session.SessionTokenHash).IsUnique().HasDatabaseName("uq_payment_sessions_session_token_hash");
            entity.HasIndex(session => session.OrderId).IsUnique().HasDatabaseName("uq_payment_sessions_order_id");
            entity.HasIndex(session => session.PanHash).HasDatabaseName("ix_payment_sessions_pan_hash");
            entity.Property(session => session.Status).HasConversion<string>().HasMaxLength(20).UseCollation(Collation);
        });
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("payments", table => table.HasCheckConstraint("payment_status", StatusCheck<PaymentStatus>()));
            entity.HasOne<PaymentSession>().WithOne().HasForeignKey<Payment>(payment => payment.SessionId)
                .HasConstraintName("fk_payments_session_id").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(payment => payment.SessionId).IsUnique().HasDatabaseName("uq_payments_session_id");
            entity.Property(payment => payment.Status).HasConversion<string>().HasMaxLength(20).UseCollation(Collation);
        });
        modelBuilder.Entity<Receipt>(entity =>
        {
            entity.ToTable("receipts", table => table.HasCheckConstraint("ck_receipts_data_json", "ISJSON(data) = 1"));
            entity.HasOne<Payment>().WithOne().HasForeignKey<Receipt>(receipt => receipt.PaymentId)
                .HasConstraintName("fk_receipts_payment_id").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(receipt => receipt.PaymentId).IsUnique().HasDatabaseName("uq_receipts_payment_id");
            entity.HasIndex(receipt => receipt.Number).IsUnique().HasDatabaseName("uq_receipts_number");
        });
        modelBuilder.Entity<SvgateLog>(entity =>
        {
            entity.ToTable("svgate_log", table =>
            {
                table.HasCheckConstraint("ck_svgate_log_request_masked_json", "ISJSON(request_masked) = 1");
                table.HasCheckConstraint("ck_svgate_log_response_masked_json", "ISJSON(response_masked) = 1 OR ISJSON('[' + response_masked + ']') = 1");
            });
            entity.HasOne<PaymentSession>().WithMany().HasForeignKey(log => log.SessionId)
                .HasConstraintName("fk_svgate_log_session_id").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(log => log.SessionId).HasDatabaseName("ix_svgate_log_session_id");
        });
        modelBuilder.Entity<HumoLog>(entity =>
        {
            entity.ToTable("humo_log", table =>
            {
                table.HasCheckConstraint("ck_humo_log_request_masked_json", "ISJSON(request_masked) = 1");
                table.HasCheckConstraint("ck_humo_log_response_masked_json", "ISJSON(response_masked) = 1");
            });
            entity.HasOne<PaymentSession>().WithMany().HasForeignKey(log => log.SessionId)
                .HasConstraintName("fk_humo_log_session_id").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(log => log.SessionId).HasDatabaseName("ix_humo_log_session_id");
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.FindPrimaryKey()?.SetName($"pk_{entityType.GetTableName()}");
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
                if (property.Name == nameof(Base.Id))
                {
                    property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
        }
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow();
        foreach (var entry in ChangeTracker.Entries<Base>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }
                entry.Entity.UpdatedAt ??= now;
            }
            else if (entry.State == EntityState.Modified && !entry.Property(item => item.UpdatedAt).IsModified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public async Task<PaymentSession?> LockedSessionAsync(Guid id)
    {
        var fresh = await PaymentSessions
            .FromSql($"SELECT * FROM payment_sessions WITH (UPDLOCK, ROWLOCK) WHERE id = {id}")
            .AsNoTracking()
            .SingleOrDefaultAsync();
        return fresh is null ? null : Adopt(fresh);
    }

    public async Task<Payment?> LockedPaymentAsync(Guid sessionId)
    {
        var fresh = await Payments
            .FromSql($"SELECT * FROM payments WITH (UPDLOCK, ROWLOCK) WHERE session_id = {sessionId}")
            .AsNoTracking()
            .SingleOrDefaultAsync();
        return fresh is null ? null : Adopt(fresh);
    }

    public async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync();
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
                continue;
            }
            await entry.ReloadAsync();
        }
    }

    public async Task RefreshAsync(object entity)
    {
        var entry = Entry(entity);
        if (entry.State != EntityState.Detached)
        {
            await entry.ReloadAsync();
        }
    }

    private T Adopt<T>(T fresh) where T : Base
    {
        var tracked = Set<T>().Local.FirstOrDefault(entity => entity.Id == fresh.Id);
        if (tracked is null)
        {
            Set<T>().Attach(fresh);
            return fresh;
        }
        var entry = Entry(tracked);
        entry.CurrentValues.SetValues(fresh);
        entry.OriginalValues.SetValues(fresh);
        entry.State = EntityState.Unchanged;
        return tracked;
    }

    private static string StatusCheck<TEnum>() where TEnum : struct, Enum =>
        $"status IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";
}

public sealed class JsonObjectConverter() : ValueConverter<JsonObject, string>(
    value => value.ToJsonString(Domain.Schemas.WireJson.Options),
    value => JsonNode.Parse(value, null, default)!.AsObject());

public sealed class JsonNodeConverter() : ValueConverter<JsonNode, string>(
    value => value.ToJsonString(Domain.Schemas.WireJson.Options),
    value => JsonNode.Parse(value, null, default)!);

public sealed class JsonNodeComparer<T>() : ValueComparer<T>(
    (left, right) => JsonNode.DeepEquals(left, right),
    value => value.ToJsonString(Domain.Schemas.WireJson.Options).GetHashCode(StringComparison.Ordinal),
    value => (T)JsonNode.Parse(value.ToJsonString(Domain.Schemas.WireJson.Options), null, default)!) where T : JsonNode;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PaymentDbContext>()
            .UseSqlServer("Server=localhost;Database=payment;Trusted_Connection=False;TrustServerCertificate=True")
            .Options,
        TimeProvider.System);
}
