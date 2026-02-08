using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Crm.Webhook.Core.Services.Hubs
{
    public class CrmHub : Hub
    {
        private readonly ILogger<CrmHub> _logger;
        // puedes añadir métodos para que clientes llamen, por ejemplo:
        public Task SendToGroup(string group, string method, object payload)
            => Clients.Group(group).SendAsync(method, payload);

        // Método para que el cliente se una a su grupo de negocio
        public async Task JoinGroup(string businessAccountId)
        {
            if (!string.IsNullOrEmpty(businessAccountId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, businessAccountId);

                _logger.LogInformation("[WEBHOOKAPI][CRM.WEBHOOK.CORE][SERVICES][HUBS][CRMHUB].[JoinGroup] INFO | Cliente {ConnectionId} unido al grupo: {Group}",
                    Context.ConnectionId, businessAccountId);
            }
        }

        // Método para recibir mensajes enviados desde el CRM
        public async Task NewMessageSent(string groupName, object message)
        {
            _logger.LogInformation("[WEBHOOKAPI][CRM.WEBHOOK.CORE][SERVICES][HUBS][CRMHUB].[NewMessageSent] PROCESO | Retransmitiendo mensaje al grupo: {Group}", groupName);

            // Retransmitir a todos en el grupo (incluyendo otros agentes)
            // excepto al que lo envió (porque él ya lo refrescó localmente)
            await Clients.OthersInGroup(groupName).SendAsync("NewMessage", message);
        }

        public async Task ChatAssigned(string groupName, object data)
        {
            _logger.LogInformation("[WEBHOOKAPI][CRM.WEBHOOK.CORE][SERVICES][HUBS][CRMHUB].[ChatAssigned] PROCESO | Notificando asignación de chat en grupo: {Group}", groupName);

            // Reenvía la información a todos los demás en el grupo
            await Clients.Group(groupName).SendAsync("ChatAssigned", data);
        }

        public async Task NotifyUpdate(string groupName, object data)
        {
            _logger.LogInformation("[WEBHOOKAPI][CRM.WEBHOOK.CORE][SERVICES][HUBS][CRMHUB].[NotifyUpdate] PROCESO | Orden de refresco enviada al grupo: {Group}", groupName);

            // Esto asegura que TODOS los agentes suscritos al grupo reciban la orden de refrescar
            await Clients.Group(groupName).SendAsync("NewMessage", data);
        }

        // Para permitir notificaciones de edición entre agentes
        public async Task MessageUpdated(string groupName, object data)
        {
            _logger.LogInformation("[WEBHOOKAPI].[CRMHUB].[MessageUpdated] PROCESO | Notificando edición de mensaje en grupo: {Group}", groupName);

            await Clients.OthersInGroup(groupName).SendAsync("MessageUpdated", data);
        }


        // Métodos para saber cuando alguien se conecta/desconecta
        public override Task OnConnectedAsync()
        {
            _logger.LogInformation("[WEBHOOKAPI].[CRMHUB].[OnConnected] CONEXIÓN | Nuevo cliente conectado: {ConnectionId}", Context.ConnectionId);
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogWarning("[WEBHOOKAPI].[CRMHUB].[OnDisconnected] CONEXIÓN | Cliente desconectado: {ConnectionId}", Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
