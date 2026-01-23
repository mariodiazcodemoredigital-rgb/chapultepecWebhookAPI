using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Dtos
{
    public class ThreadSummaryDto
    {
        public string ThreadId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? LastMessagePreview { get; set; }
        public DateTime? LastMessageUtc { get; set; }
        public int UnreadCount { get; set; }
        public int Channel { get; set; }
        public string? AssignedTo { get; set; }
        public string? PhotoUrl { get; set; }
        public bool IsActive { get; set; }
    }
}
