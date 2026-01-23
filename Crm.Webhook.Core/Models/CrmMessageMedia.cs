using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public class CrmMessageMedia
    {
        public int Id { get; set; }

        // 🔗 FK
        public int MessageId { get; set; }
        public CrmMessage Message { get; set; } = null!;

        // 📦 Tipo de media
        // document | image | audio | video | sticker
        public string MediaType { get; set; } = null!;

        // 📄 Metadata general
        public string? MimeType { get; set; }
        public string? FileName { get; set; }
        public long? FileLength { get; set; }
        public int? PageCount { get; set; }

        // 🌐 URLs (NO son públicas)
        public string? MediaUrl { get; set; }
        public string? DirectPath { get; set; }

        // 🔐 Crypto (OBLIGATORIO para desencriptar)
        public string MediaKey { get; set; } = null!;
        public string FileSha256 { get; set; } = null!;
        public string FileEncSha256 { get; set; } = null!;
        public long? MediaKeyTimestamp { get; set; }

        // 🖼 Preview (base64) 
        public string? ThumbnailBase64 { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public int statustest { get; set; }
    }
}
