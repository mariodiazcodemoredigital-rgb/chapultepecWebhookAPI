using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public enum MessageKind
    {
        Text = 0,
        Image = 1,
        Document = 2,
        Audio = 3,
        Sticker = 4,
        Video = 5
    }
}
