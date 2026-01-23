using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Data.Repositories.EvolutionWebHook
{
    public class WebhookControlRepository
    {
        private readonly IDbContextFactory<CrmInboxDbContext> _factory;

        public WebhookControlRepository(IDbContextFactory<CrmInboxDbContext> factory)
        {
            _factory = factory;
        }

        public async Task<bool> IsEnabledAsync(string name, CancellationToken ct = default)
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            return await db.WebhookControls
                .AsNoTracking()
                .Where(x => x.Name == name)
                .Select(x => x.Enabled)
                .FirstOrDefaultAsync(ct);
        }

        public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default)
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            var row = await db.WebhookControls.SingleAsync(x => x.Name == name, ct);
            row.Enabled = enabled;
            row.UpdatedUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }
    }
}
