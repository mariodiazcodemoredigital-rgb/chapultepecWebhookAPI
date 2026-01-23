using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public class WebhookControl
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public bool Enabled { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
