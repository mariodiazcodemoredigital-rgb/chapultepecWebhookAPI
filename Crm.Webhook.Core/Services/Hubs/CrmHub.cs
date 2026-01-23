using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Hubs
{
    public class CrmHub : Hub
    {
        // puedes añadir métodos para que clientes llamen, por ejemplo:
        public Task SendToGroup(string group, string method, object payload)
            => Clients.Group(group).SendAsync(method, payload);

        // Método para que el cliente se una a su grupo de negocio
        public async Task JoinGroup(string businessAccountId)
        {
            if (!string.IsNullOrEmpty(businessAccountId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, businessAccountId);
                Console.WriteLine($"Cliente {Context.ConnectionId} unido al grupo: {businessAccountId}");
            }
        }

        // Nuevo método para recibir mensajes enviados desde el CRM
        public async Task NewMessageSent(string groupName, object message)
        {
            // Retransmitir a todos en el grupo (incluyendo otros agentes)
            // excepto al que lo envió (porque él ya lo refrescó localmente)
            await Clients.OthersInGroup(groupName).SendAsync("NewMessage", message);
        }

        public async Task ChatAssigned(string groupName, object data)
        {
            // Reenvía la información a todos los demás en el grupo
            await Clients.Group(groupName).SendAsync("ChatAssigned", data);
        }

        public async Task NotifyUpdate(string groupName, object data)
        {
            // Esto asegura que TODOS los agentes suscritos al grupo reciban la orden de refrescar
            await Clients.Group(groupName).SendAsync("NewMessage", data);
        }

        // Agrega esto a CrmHub.cs para permitir notificaciones de edición entre agentes
        public async Task MessageUpdated(string groupName, object data)
        {
            await Clients.OthersInGroup(groupName).SendAsync("MessageUpdated", data);
        }
    }
}
