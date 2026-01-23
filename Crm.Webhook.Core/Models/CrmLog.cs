using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    [Table("CrmLogs")]
    public class CrmLog
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string LogType { get; set; } = string.Empty; // CHAT_ASSIGNMENT, CHAT_DELETE, SYSTEM_SIGNALR, SYSTEM_EVOLUTION

        [MaxLength(100)]
        public string Action { get; set; } = string.Empty; // ASSIGNED, SOFT_DELETE, RECONNECTED, etc.

        public string? ThreadId { get; set; } // Opcional, nulo para eventos de sistema

        [Required]
        [MaxLength(100)]
        public string User { get; set; } = "SYSTEM";

        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [Required]
        public string Description { get; set; } = string.Empty;

        public string? Metadata { get; set; } // Para guardar JSON o datos técnicos extra

        public string? IpAddress { get; set; }
    }
}
