using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public enum InboxFilter { Todos, Mios, SinAsignar, Equipo }

    public enum SenderKind { Agent, Customer, System }

    public enum ChannelKind
    {
        Unknown = 0,
        WhatsApp = 1,
        Messenger = 2,
        Instagram = 3,
        WebChat = 4
    }

    public sealed record CustomerIdentity(
            ChannelKind Channel,
            string? DisplayName,
            string? Phone,
            string? Email,
            string? PlatformId,        // PSID, IG user id, etc.
            string BusinessAccountId   // antes BusinessPhoneId
        );

    public class ChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public SenderKind Kind { get; set; }
        public string Sender { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Text { get; set; } = "";
        public string? MediaUrl { get; set; }
        public string? MediaMime { get; set; }
        public string? WaMessageId { get; set; }
        public string? RawPayloadJson { get; set; }
    }
}
