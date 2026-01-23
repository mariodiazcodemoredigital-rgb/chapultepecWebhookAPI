using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public class EvolutionRawPayload
    {
        public int Id { get; set; }

        public string ThreadId { get; set; } = default!;

        public string Source { get; set; } = "evolution";

        public string? Instance { get; set; }

        public string? Event { get; set; }

        public string? MessageType { get; set; }

        public string? RemoteJid { get; set; }

        public bool? FromMe { get; set; }

        public string? Sender { get; set; }

        public string? CustomerPhone { get; set; }

        public string? CustomerDisplayName { get; set; }

        public DateTime? MessageDateUtc { get; set; }

        public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;

        public string PayloadJson { get; set; } = default!;

        public bool Processed { get; set; } = false;

        public string? Notes { get; set; }
    }
}
