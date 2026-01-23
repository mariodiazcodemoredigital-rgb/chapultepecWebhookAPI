using Crm.Webhook.Core.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook
{
    public interface IMessageQueue
    {
        ValueTask EnqueueAsync(IncomingMessageDto message);
    }
}
