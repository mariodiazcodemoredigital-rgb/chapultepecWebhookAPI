using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public class CrmMessage
    {
        public int Id { get; set; }

        // =========================
        // Relación
        // =========================
        public int ThreadRefId { get; set; }
        [JsonIgnore]
        //[ForeignKey("ThreadRefId")] // <--- ESTA ES LA CLAVE: Vincula el objeto 'Thread' con la columna 'ThreadRefId'
        public CrmThread Thread { get; set; } = null!;

        // =========================
        // Datos básicos
        // =========================
        public string Sender { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? Text { get; set; }

        // Timestamp normalizado (UTC)
        public DateTime TimestampUtc { get; set; }

        // Timestamp externo (unix, WhatsApp)
        public long? ExternalTimestamp { get; set; }

        public bool DirectionIn { get; set; }

        // =========================
        // Media
        // =========================
        public string? MediaUrl { get; set; }
        public string? MediaMime { get; set; }

        // image / audio / document / sticker / video
        public string? MediaType { get; set; }

        // Caption asociado al media (opcional)
        public string? MediaCaption { get; set; }

        // =========================
        // Auditoría / externo
        // =========================
        public string RawPayload { get; set; } = null!;

        // Evolution / WhatsApp ids
        public string? ExternalId { get; set; }
        public string? WaMessageId { get; set; }

        // Hash para idempotencia
        public string RawHash { get; set; } = null!;

        // Tipo normalizado para UI
        public int MessageKind { get; set; }
        // 0 = Text, 1 = Image, 2 = Document, 3 = Audio, 4 = Sticker, 5 = Video



        // Sugerencia UI (derivable pero útil)
        public bool HasMedia { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        //Navegacion con CrmMessageMedia
        public CrmMessageMedia? Media { get; set; }

        public string? Reaction { get; set; }

        public string? QuotedMessageId { get; set; }     // ID del mensaje original
        public string? QuotedMessageText { get; set; }   // Texto del mensaje citado
        public string? QuotedMessageSender { get; set; } // Quién envió el original (opcional)

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // 0 = Enviado (1 check gris), 1 = Entregado (2 checks grises), 2 = Leído (2 checks azules)
        public int Status { get; set; }
    }
}
