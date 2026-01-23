using Crm.Webhook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Dtos
{
    public class EvolutionMessageSnapshotDto
    {
        public string ThreadId { get; set; } = default!;
        public string BusinessAccountId { get; set; } = null!;
        public string Sender { get; set; } = default!;
        public string? CustomerDisplayName { get; set; }
        public string CustomerPhone { get; set; } = default!;
        public bool DirectionIn { get; set; }
        public MessageKind MessageKind { get; set; }
        public string TextPreview { get; set; } = default!;
        public string? Text { get; set; }
        public string? MediaUrl { get; set; }
        public string? MediaMime { get; set; }
        public string? MediaCaption { get; set; }
        public string? ExternalMessageId { get; set; }
        public long ExternalTimestamp { get; set; }
        public string? Source { get; set; }
        public string MessageType { get; set; } = default!;
        public string RawPayloadJson { get; set; } = default!;
        public DateTime CreatedAtUtc { get; set; }

        // =========================
        // Media crypto (WhatsApp)
        // =========================
        public string? MediaKey { get; set; }
        public string? FileSha256 { get; set; }
        public string? FileEncSha256 { get; set; }
        public string? DirectPath { get; set; }
        public long? MediaKeyTimestamp { get; set; }

        // =========================
        // Media metadata
        // =========================
        public string? MediaType { get; set; }   // image, document, audio, video
        public string? FileName { get; set; }
        public long? FileLength { get; set; }
        public int? PageCount { get; set; }
        public string? ThumbnailBase64 { get; set; }

        public string? CustomerLid { get; set; }

        public string? QuotedMessageId { get; set; }
        public string? QuotedMessageText { get; set; }

    }
}
