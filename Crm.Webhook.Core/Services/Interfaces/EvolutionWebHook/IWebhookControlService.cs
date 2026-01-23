using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook
{
    public interface IWebhookControlService
    {
        Task<bool> IsEvolutionEnabledAsync(CancellationToken ct = default);
        Task SetEvolutionEnabledAsync(bool enabled, CancellationToken ct = default);
    }
}
