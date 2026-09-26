using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Skylab.Cms.Domain.Entities;

namespace Skylab.Cms.Infrastructure.Storage.Configurations;

internal sealed class AccountErasureReceiptConfiguration : IEntityTypeConfiguration<AccountErasureReceipt>
{
    public void Configure(EntityTypeBuilder<AccountErasureReceipt> builder)
    {
        builder.ToTable("account_erasure_receipts", table =>
            table.HasCheckConstraint(
                "ck_account_erasure_receipts_counts_object",
                "jsonb_typeof(counts) = 'object'"));

        builder.HasKey(x => x.RequestId).HasName("pk_account_erasure_receipts");

        builder.Property(x => x.RequestId).HasColumnName("request_id").ValueGeneratedNever().HasColumnOrder(0);

        builder.Property(x => x.CompletedAt).HasColumnName("completed_at").IsRequired().HasColumnOrder(1);

        builder.Property(x => x.Counts).HasColumnName("counts").HasColumnType("jsonb").IsRequired().HasColumnOrder(2);
    }
}
