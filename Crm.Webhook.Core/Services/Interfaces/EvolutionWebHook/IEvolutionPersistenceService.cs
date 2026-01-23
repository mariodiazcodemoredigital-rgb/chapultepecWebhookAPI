using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook
{
    public interface IEvolutionPersistenceService
    {
        // Métodos principales de procesamiento
        // Método principal para procesar el snapshot completo
        Task PersistSnapshotAsync(EvolutionMessageSnapshotDto snap, CancellationToken ct = default);

        // Método para guardar el payload en bruto (Auditoría)
        Task<int> SaveRawPayloadAsync(string rawBody, string? remoteIp, CancellationToken ct = default);

        // Método de respaldo para payloads con errores
        Task SaveRawEvolutionPayloadAsync(string rawBody, string threadId, CancellationToken ct = default);



        // Métodos de lógica de negocio (TitanicGym)
        Task<CrmThread> GetOrCreateThreadAsync(EvolutionMessageSnapshotDto snap, CancellationToken ct = default);
        Task<bool> MessageExistsAsync(EvolutionMessageSnapshotDto snap, int threadId, CancellationToken ct = default);
        Task InsertMediaAsync(CrmMessage msg, EvolutionMessageSnapshotDto s, CancellationToken ct = default);
    }
}
