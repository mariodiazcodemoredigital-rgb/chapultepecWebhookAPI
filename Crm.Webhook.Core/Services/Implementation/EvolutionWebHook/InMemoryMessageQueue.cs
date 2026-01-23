using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Services.Implementation.EvolutionWebHook
{
    public class InMemoryMessageQueue : IMessageQueue, IDisposable
    {
        private readonly Channel<IncomingMessageDto> _channel = Channel.CreateUnbounded<IncomingMessageDto>();

        public ValueTask EnqueueAsync(IncomingMessageDto message) => _channel.Writer.WriteAsync(message);

        public ChannelReader<IncomingMessageDto> Reader => _channel.Reader;

        public void Dispose() => _channel.Writer.Complete();
    }
}
