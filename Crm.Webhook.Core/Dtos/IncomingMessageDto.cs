using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Dtos
{
    public record IncomingMessageDto(
        string threadId,
        string businessAccountId,
        string sender,
        string displayName,
        string text,
        long timestamp,
        bool directionIn,
        AiInfo? ai,
        string action,
        string reason,
        string title,
        string RawPayload
    );

    public record AiInfo(int channel, string pipelineName, string stageName);
}
