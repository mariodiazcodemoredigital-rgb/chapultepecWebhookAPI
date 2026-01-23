using Crm.Webhook.Core.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Data
{
    public class CrmInboxDbContext : DbContext
    {
        public CrmInboxDbContext(DbContextOptions<CrmInboxDbContext> options) : base(options) { }

        public DbSet<CrmThread> CrmThreads { get; set; } = null!;
        public DbSet<CrmMessage> CrmMessages { get; set; } = null!;
        public DbSet<CrmMessageMedia> CrmMessageMedias => Set<CrmMessageMedia>();
        public DbSet<EvolutionRawPayload> EvolutionRawPayloads => Set<EvolutionRawPayload>();
        public DbSet<WebhookControl> WebhookControls => Set<WebhookControl>();
        public DbSet<CrmLog> CrmLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<CrmThread>()
                .HasIndex(t => t.ThreadId)
                .IsUnique();

            modelBuilder.Entity<CrmMessage>()
                .HasIndex(m => new { m.ExternalId })
                .HasDatabaseName("IX_CrmMessage_ExternalId");

            modelBuilder.Entity<CrmMessage>()
                .HasIndex(m => new { m.RawHash })
                .HasDatabaseName("IX_CrmMessage_RawHash");

            // Relaciones
            modelBuilder.Entity<CrmMessage>()
                .HasOne(m => m.Thread)
                .WithMany(t => t.Messages)
                .HasForeignKey(m => m.ThreadRefId)
                .OnDelete(DeleteBehavior.Cascade);

          

            modelBuilder.Entity<CrmLog>()
                .HasIndex(l => l.ThreadId);

            modelBuilder.Entity<CrmLog>()
                .HasIndex(l => l.Timestamp);
        }
    }
}
