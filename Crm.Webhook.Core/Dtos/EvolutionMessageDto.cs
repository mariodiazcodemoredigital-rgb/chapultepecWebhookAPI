using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Dtos
{
    public class EvolutionMessageDto
    {
        [JsonPropertyName("key")]
        public EvolutionMessageKey Key { get; set; } = new();

        [JsonPropertyName("pushName")]
        public string? PushName { get; set; }

        [JsonPropertyName("messageType")]
        public string? MessageType { get; set; }

        [JsonPropertyName("message")]
        public EvolutionMessageContent? Message { get; set; }

        [JsonPropertyName("messageTimestamp")]
        public long MessageTimestamp { get; set; }
    }

    public class EvolutionMessageKey
    {
        [JsonPropertyName("remoteJid")]
        public string RemoteJid { get; set; } = string.Empty;

        [JsonPropertyName("fromMe")]
        public bool FromMe { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    public class EvolutionMessageContent
    {
        // Para mensajes de texto simple
        [JsonPropertyName("conversation")]
        public string? Conversation { get; set; }

        // Para mensajes con formato o respuestas
        [JsonPropertyName("extendedTextMessage")]
        public ExtendedTextMessage? ExtendedTextMessage { get; set; }

        // Para imágenes, videos, audios y documentos
        [JsonPropertyName("imageMessage")]
        public MediaMessage? ImageMessage { get; set; }

        [JsonPropertyName("videoMessage")]
        public MediaMessage? VideoMessage { get; set; }

        [JsonPropertyName("audioMessage")]
        public MediaMessage? AudioMessage { get; set; }

        [JsonPropertyName("documentMessage")]
        public MediaMessage? DocumentMessage { get; set; }

        [JsonPropertyName("stickerMessage")]
        public MediaMessage? StickerMessage { get; set; }

        [JsonPropertyName("reactionMessage")]
        public EvolutionReactionMessage? ReactionMessage { get; set; }
    }

    // Clase para mapear la reacción
    public class EvolutionReactionMessage
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; } // El emoji: ❤️, 😂, etc.

        [JsonPropertyName("key")]
        public EvolutionMessageKey? Key { get; set; } // Referencia al mensaje original
    }


    public class MediaMessage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("mimetype")]
        public string? Mimetype { get; set; }

        [JsonPropertyName("caption")]
        public string? Caption { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }
    }

    public class EvolutionResponseRoot
    {
        // Cambiamos el nombre para que coincida con el JSON que enviaste
        [JsonPropertyName("messages")]
        public EvolutionMessagesContainer Messages { get; set; }
    }

    public class EvolutionMessagesContainer
    {
        public int Total { get; set; }
        // Aquí es donde realmente viven los datos
        public List<EvolutionMessage> Records { get; set; }
    }

    public class EvolutionMessage
    {
        public Key Key { get; set; }
        public string PushName { get; set; }
        public string MessageType { get; set; }
        public MessageContent Message { get; set; }
        public long MessageTimestamp { get; set; }
    }

    public class Key
    {
        public string Id { get; set; }
        public bool FromMe { get; set; }
        public string RemoteJid { get; set; }
    }

    public class MessageContent
    {
        public string Conversation { get; set; } // Mensajes simples
        public ExtendedTextMessage ExtendedTextMessage { get; set; } // Mensajes Web/Desktop
        public ReactionMessage ReactionMessage { get; set; }
        public ImageMessage ImageMessage { get; set; }
    }

    public class ExtendedTextMessage { public string Text { get; set; } }
    public class ReactionMessage { public string Text { get; set; } }
    public class ImageMessage { public string Caption { get; set; } }
}
