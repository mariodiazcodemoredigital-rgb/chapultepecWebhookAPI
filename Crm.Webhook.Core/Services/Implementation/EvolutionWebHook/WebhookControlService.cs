using Crm.Webhook.Core.Data.Repositories.EvolutionWebHook;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Implementation.EvolutionWebHook
{
    public class WebhookControlService : IWebhookControlService
    {
        private readonly WebhookControlRepository _repo;
        private bool? _cached;
        private DateTime _lastRead;

        public WebhookControlService(WebhookControlRepository repo)
        {
            _repo = repo;
        }

        public async Task<bool> IsEvolutionEnabledAsync(CancellationToken ct = default)
        {
            if (_cached.HasValue && (DateTime.UtcNow - _lastRead).TotalSeconds < 10)
                return _cached.Value;

            _cached = await _repo.IsEnabledAsync("EvolutionWebhook", ct);
            _lastRead = DateTime.UtcNow;
            return _cached.Value;
        }

        public async Task SetEvolutionEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            await _repo.SetEnabledAsync("EvolutionWebhook", enabled, ct);
            _cached = enabled;
            _lastRead = DateTime.UtcNow;
        }
    }
}
