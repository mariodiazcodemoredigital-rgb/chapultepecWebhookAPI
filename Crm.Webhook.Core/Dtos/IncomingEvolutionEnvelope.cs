using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Dtos
{
    public class IncomingEvolutionEnvelope
    {
        // 🔹 Identidad
        public string ThreadId { get; set; } = default!;
        public string BusinessAccountId { get; set; } = default!;
        public string Channel { get; set; } = "whatsapp";

        // 🔹 Cliente
        public string? CustomerPhone { get; set; }
        public string? CustomerDisplayName { get; set; }
        public string? CustomerPlatformId { get; set; } // remoteJid

        // 🔹 Mensaje
        public string? Text { get; set; }
        public bool DirectionIn { get; set; }
        public long ExternalTimestamp { get; set; }
        public string? ExternalMessageId { get; set; }

        // 🔹 Media (aún no procesada)
        public string? MediaUrl { get; set; }
        public string? MediaMime { get; set; }

        // 🔹 Control de UI / negocio
        public string? LastMessagePreview { get; set; }
        public int UnreadCount { get; set; } = 1;
        public int Status { get; set; } = 0; // pendiente
        public string? AssignedTo { get; set; }
        public string? CustomerLid { get; set; }

        // 🔹 Auditoría
        public string RawPayloadJson { get; set; } = default!;
        public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    }
}
