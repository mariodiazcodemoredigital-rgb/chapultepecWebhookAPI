using Crm.Webhook.Core.Data.Repositories.EvolutionWebHook;
using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Implementation.EvolutionWebHook
{
    public class EvolutionPersistenceService : IEvolutionPersistenceService
    {
        private readonly EvolutionPersistenceRepository _evolutionRepository;

        public EvolutionPersistenceService(EvolutionPersistenceRepository evolutionRepository)
        {
            _evolutionRepository = evolutionRepository;
        }

        public Task PersistSnapshotAsync(EvolutionMessageSnapshotDto snap, CancellationToken ct = default)
        {
            // Nota: Asegúrate de que el método PersistSnapshotAsync en tu Repositorio sea PUBLIC
            return _evolutionRepository.PersistSnapshotAsync(snap, ct);
        }

        public Task<int> SaveRawPayloadAsync(string rawBody, string? remoteIp, CancellationToken ct = default)
        {
            // Nota: Asegúrate de que este método en tu Repositorio sea PUBLIC
            return _evolutionRepository.SaveRawPayloadAsync(rawBody, remoteIp, ct);
        }

        public Task SaveRawEvolutionPayloadAsync(string rawBody, string threadId, CancellationToken ct = default)
        {
            // Nota: Asegúrate de que este método en tu Repositorio sea PUBLIC
            return _evolutionRepository.SaveRawEvolutionPayloadAsync(rawBody, threadId, ct);
        }

        public Task<CrmThread> GetOrCreateThreadAsync(EvolutionMessageSnapshotDto snap, CancellationToken ct = default)
        {
            return _evolutionRepository.GetOrCreateThreadAsync(snap, ct);
        }

        public Task<bool> MessageExistsAsync(EvolutionMessageSnapshotDto snap, int threadId, CancellationToken ct = default)
        {
            return _evolutionRepository.MessageExistsAsync(snap, threadId, ct);
        }

        public Task InsertMediaAsync(CrmMessage msg, EvolutionMessageSnapshotDto s, CancellationToken ct = default)
        {
            return _evolutionRepository.InsertMediaAsync(msg, s, ct);
        }
    }
}
