using Crm.Webhook.Core.Data;
using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using Crm.Webhook.Core.Parsers;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Implementation.EvolutionWebHook
{
    public class MessageProcessingService : BackgroundService
    {
        private readonly InMemoryMessageQueue _queue;
        private readonly IServiceProvider _sp;
        private readonly ILogger<MessageProcessingService> _log;

        public MessageProcessingService(
            InMemoryMessageQueue queue,
            IServiceProvider sp,
            ILogger<MessageProcessingService> log)
        {
            _queue = queue;
            _sp = sp;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {            
            // Inicio del Worker
            _log.LogInformation("[WEBHOOKAPI].[MESSAGEPROCESSINGSERVICE].[ExecuteAsync] INFO | 🚀 Worker activo procesando cola de mensajes...");

            await foreach (var msg in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // Creamos un Scope para obtener servicios Scoped (Parser, DB, etc)
                    using var scope = _sp.CreateScope();
                    var parser = scope.ServiceProvider.GetRequiredService<EvolutionParser>();
                    var persistence = scope.ServiceProvider.GetRequiredService<IEvolutionPersistenceService>();

                    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CrmInboxDbContext>>();

                    // 1. El Parser hace el trabajo sucio de procesar el JSON
                    var snap = parser.BuildSnapshot(msg.RawPayload);
                    if (snap == null)
                    {
                        _log.LogWarning("[WEBHOOKAPI].[MESSAGEPROCESSINGSERVICE].[ExecuteAsync] ADVERTENCIA | ⚠️ No se pudo generar el snapshot para un mensaje. Thread: {ThreadId}", msg.threadId);                        
                        continue;
                    }

                    // 2. Persistencia real
                    if (snap != null)
                    {
                        _log.LogInformation("[WEBHOOKAPI].[MESSAGEPROCESSINGSERVICE].[ExecuteAsync] PROCESO | Procesando Snapshot");

                        await persistence.PersistSnapshotAsync(snap, stoppingToken);

                        _log.LogInformation("[WEBHOOKAPI].[MESSAGEPROCESSINGSERVICE].[ExecuteAsync] ÉXITO | Snapshot persistido correctamente en DB.");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[WEBHOOKAPI].[MESSAGEPROCESSINGSERVICE].[ExecuteAsync] EXCEPCIÓN CRÍTICA | Error procesando mensaje en segundo plano. Thread: {ThreadId}", msg.threadId);                    
                }
            }
        }

      
    }
}
