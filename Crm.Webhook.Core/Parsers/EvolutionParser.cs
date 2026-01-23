using Crm.Webhook.Core.Data;
using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Parsers
{
    public class EvolutionParser
    {
        private readonly ILogger<EvolutionParser> _log;

        public EvolutionParser(ILogger<EvolutionParser> log)
        {
            _log = log;
        }

        // Método de mapeo (añádelo dentro del controller)
        public IncomingEvolutionEnvelope? MapEvolutionToEnvelope(string rawBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;

                JsonElement dataElem = root.TryGetProperty("data", out var d) ? d : root;

                string? remoteJid = null;
                string? senderPn = null;
                string? pushName = null;
                string? messageText = null;
                string? externalMessageId = null;
                bool fromMe = false;
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (dataElem.TryGetProperty("key", out var key))
                {
                    if (key.TryGetProperty("remoteJid", out var rj)) remoteJid = rj.GetString();
                    if (key.TryGetProperty("id", out var id)) externalMessageId = id.GetString();
                    if (key.TryGetProperty("fromMe", out var fm)) fromMe = fm.GetBoolean();
                    if (key.TryGetProperty("senderPn", out var spn)) senderPn = spn.GetString();
                }

                if (root.TryGetProperty("pushName", out var pn))
                    pushName = pn.GetString();

                if (dataElem.TryGetProperty("messageTimestamp", out var ts) && ts.TryGetInt64(out var tsv))
                    timestamp = tsv;

                if (dataElem.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("conversation", out var c))
                        messageText = c.GetString();
                    else if (msg.TryGetProperty("extendedTextMessage", out var e) &&
                             e.TryGetProperty("text", out var t))
                        messageText = t.GetString();
                }

                // --- LÓGICA DE UNIFICACIÓN DE LID ---
                string? finalPhone = null;
                string? finalLid = null;

                if (remoteJid != null)
                {
                    if (remoteJid.Contains("@s.whatsapp.net"))
                    {
                        finalPhone = remoteJid.Replace("@s.whatsapp.net", "").Replace("@c.us", "");
                    }
                    else if (remoteJid.Contains("@lid"))
                    {
                        //  REGLA DE ORO: Si es LID, NO es teléfono.
                        finalLid = remoteJid;

                        // Solo si Evolution nos da el senderPn, tenemos el teléfono real
                        if (senderPn != null && senderPn.Contains("@s.whatsapp.net"))
                        {
                            finalPhone = senderPn.Replace("@s.whatsapp.net", "");
                        }
                    }
                }

                // El ThreadId debe ser amigable: si no hay teléfono, usamos el LID literal
                var threadId = !string.IsNullOrEmpty(finalPhone)
                               ? $"wa:{finalPhone}"
                               : $"wa:lid:{finalLid?.Replace("@lid", "")}";

                return new IncomingEvolutionEnvelope
                {
                    ThreadId = threadId,
                    BusinessAccountId = root.GetProperty("instance").GetString() ?? "evolution",
                    CustomerPhone = finalPhone,
                    CustomerDisplayName = pushName,
                    CustomerPlatformId = remoteJid,
                    Text = messageText,
                    LastMessagePreview = messageText?.Length > 200 ? messageText[..200] : messageText,
                    DirectionIn = !fromMe,
                    ExternalTimestamp = timestamp,
                    ExternalMessageId = externalMessageId,
                    UnreadCount = !fromMe ? 1 : 0,
                    RawPayloadJson = rawBody,
                    CustomerLid = finalLid
                };
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to map Evolution payload to Envelope");
                return null;
            }
        }

        // Construye el Dto que se guardara en la tabla
        public EvolutionMessageSnapshotDto? BuildSnapshot(string rawBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;
                var data = root.TryGetProperty("data", out var d) ? d : root;

                // =========================
                // Identidad base
                // =========================
                if (!root.TryGetProperty("instance", out var instProp))
                    return null;

                var instance = instProp.GetString()!;
                var senderRoot = root.TryGetProperty("sender", out var s) ? s.GetString() : null;

                // key
                if (!data.TryGetProperty("key", out var key))
                {
                    // Si no está en data.key, revisamos si es una reacción
                    if (data.TryGetProperty("message", out var msgNode) &&
                        msgNode.TryGetProperty("reactionMessage", out var reactNode))
                    {
                        key = reactNode.GetProperty("key"); // Esta es la key del mensaje original
                    }
                    else
                    {
                        return null; // Si no hay key de ningún tipo, no es un mensaje procesable
                    }
                }

                if (!key.TryGetProperty("remoteJid", out var rj))
                    return null;

                var remoteJid = rj.GetString()!;

                //  EXTRAER senderPn PARA VALIDACIÓN DE LID
                string? senderPn = key.TryGetProperty("senderPn", out var spn) ? spn.GetString() : null;

                var fromMe = key.TryGetProperty("fromMe", out var fm) && fm.GetBoolean();
                var externalMessageId = key.TryGetProperty("id", out var kid) ? kid.GetString() : null;

                // --- CAMBIO AQUÍ: Capturamos el senderLid si existe ---
                string? senderLid = key.TryGetProperty("senderLid", out var sl) ? sl.GetString() : null;

                string? pushName = null;
                if (data.TryGetProperty("pushName", out var pn))
                    pushName = pn.GetString();

                //  DETECCIÓN AVANZADA DE ORIGEN (Prospecto vs Contacto LID)
                bool isFromAd = false;

                // Variables para almacenar el citado 
                string? quotedId = null;
                string? quotedText = null;

                // Verificamos que la propiedad exista Y que no sea nula en el JSON
                if (data.TryGetProperty("contextInfo", out var context) && context.ValueKind != JsonValueKind.Null)
                {
                    // --- LÓGICA DE CITADOS (REPLIES) ---
                    // 1. Obtener el ID del mensaje original
                    if (context.TryGetProperty("stanzaId", out var stanza))
                        quotedId = stanza.GetString();

                    // 2. Obtener el contenido del mensaje citado
                    if (context.TryGetProperty("quotedMessage", out var qMsg))
                    {
                        if (qMsg.TryGetProperty("conversation", out var c))
                            quotedText = c.GetString();
                        else if (qMsg.TryGetProperty("extendedTextMessage", out var etm))
                            quotedText = etm.GetProperty("text").GetString();
                        // Si el citado es una imagen, podrías poner "[Imagen]"
                        else if (qMsg.TryGetProperty("imageMessage", out _))
                            quotedText = "📷 Foto";
                    }

                    // 1. Verificamos saludo automático
                    if (context.TryGetProperty("automatedGreetingMessageShown", out var autoGreet) && autoGreet.ValueKind != JsonValueKind.Null)
                    {
                        // Usamos GetBoolean solo si el tipo es efectivamente Booleano para evitar excepciones
                        if (autoGreet.ValueKind == JsonValueKind.True || autoGreet.ValueKind == JsonValueKind.False)
                            isFromAd = autoGreet.GetBoolean();
                    }

                    // 2. Verificamos Ad Reply
                    if (!isFromAd && context.TryGetProperty("externalAdReply", out var adReply) && adReply.ValueKind != JsonValueKind.Null)
                    {
                        isFromAd = true;
                    }
                }

                //  LÓGICA DE EXTRACCIÓN DE NÚMERO (Validación @s.whatsapp.net)
                //  LÓGICA DE IDENTIDAD (UNIFICADA)
                string? finalPhone = null;
                string? finalLid = null;

                if (remoteJid.Contains("@s.whatsapp.net"))
                {
                    finalPhone = remoteJid.Replace("@s.whatsapp.net", "").Replace("@c.us", "");
                    // Si el remoteJid es número, el LID viene en senderLid
                    finalLid = senderLid;
                }
                else if (remoteJid.Contains("@lid"))
                {
                    finalLid = remoteJid;
                    if (senderPn != null && senderPn.Contains("@s.whatsapp.net"))
                        finalPhone = senderPn.Replace("@s.whatsapp.net", "");
                }

                // DETERMINACIÓN DEL NOMBRE MOSTRADO
                // Regla: 
                // 1. Si es un mensaje saliente (fromMe), el pushName es el tuyo, así que NO lo usamos para el cliente.
                // 2. Si es entrante y es anuncio -> "Prospecto de Anuncio"
                // 3. Si es entrante y es LID pero no anuncio -> "Contacto LID (Web/Otro)"
                // 4. Si tenemos PushName del cliente (cuando es entrante), usamos ese.

                string? computedDisplayName = pushName;

                if (fromMe)
                {
                    // No queremos que el chat se llame "Mario Diaz" (tu nombre) solo porque tú iniciaste
                    computedDisplayName = isFromAd ? "Prospecto de Anuncio" : "Contacto LID (Pendiente)";
                }
                else
                {
                    // Es un mensaje que VIENE del cliente
                    if (string.IsNullOrEmpty(pushName))
                    {
                        computedDisplayName = isFromAd ? "Prospecto de Anuncio" : "Contacto LID";
                    }
                }

                // El ThreadId del snapshot debe ser consistente con la búsqueda
                var threadId = !string.IsNullOrEmpty(finalPhone)
                    ? $"wa:{finalPhone}"
                    : $"wa:lid:{finalLid?.Replace("@lid", "")}";

                // =========================
                // timestamp (string o number)
                // =========================
                if (!data.TryGetProperty("messageTimestamp", out var tsElement))
                    return null;

                // 1. Definir la zona horaria (igual que en el método anterior)
                TimeZoneInfo mexicoZone;
                try
                {
                    mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)");
                }
                catch
                {
                    mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
                }

                // 2. Obtener el timestamp y convertir
                var timestamp = ReadUnixTimestamp(tsElement);

                // 3. Convertir de Unix a UTC y luego a la hora local de México
                var utcDateTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                var createdLocal = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, mexicoZone);

                // =========================
                // Source
                // =========================
                var source =
                    data.TryGetProperty("source", out var src) ? src.GetString() :
                    root.TryGetProperty("source", out var src2) ? src2.GetString() :
                    null;

                // =========================
                // Mensaje
                // =========================
                var messageType =
                    data.TryGetProperty("messageType", out var mt) ? mt.GetString() :
                    data.TryGetProperty("type", out var t) ? t.GetString() :
                    "unknown";

                if (!data.TryGetProperty("message", out var message))
                {
                    _log.LogWarning("Payload without message node");
                    return null;
                }

                string textPreview = "[Mensaje]"; // Inicializada
                string? text = null;
                string? mediaUrl = null;
                string? mediaMime = null;
                string? mediaCaption = null;
                MessageKind messageKind = MessageKind.Text; // Inicializada

                string? mediaKey = null;
                string? fileSha256 = null;
                string? fileEncSha256 = null;
                string? directPath = null;
                long? mediaKeyTimestamp = null;
                string? fileName = null;
                long? fileLength = null;
                int? pageCount = null;
                string? thumbnailBase64 = null;
                string? mediaType = null;

                switch (messageType)
                {
                    case "conversation":
                        messageKind = MessageKind.Text;
                        text = message.GetProperty("conversation").GetString();
                        textPreview = text ?? "";
                        break;

                    case "imageMessage":
                        {
                            messageKind = MessageKind.Image;
                            mediaType = "image";

                            // Accedemos al nodo imageMessage
                            if (!message.TryGetProperty("imageMessage", out var img))
                            {
                                _log.LogWarning("Payload marcado como imageMessage pero no se encontró el nodo interno.");
                                return null;
                            }

                            // Extraer URLs (Evolution a veces pone la URL de descarga directa aquí)
                            var rawUrl = img.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                            var dPath = img.TryGetProperty("directPath", out var dpProp) ? dpProp.GetString() : null;

                            // Si la URL es de web.whatsapp (caduca rápido), preferimos armar la de mmg con el directPath
                            if ((string.IsNullOrEmpty(rawUrl) || rawUrl.Contains("web.whatsapp.net")) && !string.IsNullOrEmpty(dPath))
                                mediaUrl = $"https://mmg.whatsapp.net{dPath}";
                            else
                                mediaUrl = rawUrl;

                            // Metadatos para desencriptar (Cruciales)
                            mediaMime = img.TryGetProperty("mimetype", out var mime) ? mime.GetString() : "image/jpeg";
                            mediaKey = img.TryGetProperty("mediaKey", out var mk) ? mk.GetString() : null;
                            fileSha256 = img.TryGetProperty("fileSha256", out var fsh) ? fsh.GetString() : null;
                            fileEncSha256 = img.TryGetProperty("fileEncSha256", out var feh) ? feh.GetString() : null;
                            directPath = dPath;

                            // Caption (Si la imagen lleva texto abajo)
                            mediaCaption = img.TryGetProperty("caption", out var cap) ? cap.GetString() : null;

                            // Thumbnail (Miniatura en Base64 que viene en el JSON)
                            thumbnailBase64 = img.TryGetProperty("jpegThumbnail", out var thumb) ? thumb.GetString() : null;

                            // Tamaño del archivo
                            if (img.TryGetProperty("fileLength", out var fl))
                            {
                                if (fl.ValueKind == JsonValueKind.String && long.TryParse(fl.GetString(), out var len))
                                    fileLength = len;
                                else if (fl.ValueKind == JsonValueKind.Number)
                                    fileLength = fl.GetInt64();
                            }

                            // Preview para la lista de chats
                            textPreview = !string.IsNullOrEmpty(mediaCaption) ? $"📷 {mediaCaption}" : "📷 Foto";
                            text = mediaCaption; // Guardamos el caption como el texto del mensaje
                            break;
                        }

                    case "audioMessage":
                        {
                            messageKind = MessageKind.Audio; // Asegúrate que tu Enum MessageKind tenga Audio
                            mediaType = "audio";

                            // Acceder al nodo audioMessage
                            // Intentamos obtener el nodo de audio de forma segura
                            // A veces viene dentro de 'message', a veces 'message' es el objeto directamente
                            JsonElement aud;
                            if (message.TryGetProperty("audioMessage", out var audNode))
                            {
                                aud = audNode;
                            }
                            else
                            {
                                aud = message;
                            }

                            var rawUrl = aud.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                            var dPath = aud.TryGetProperty("directPath", out var dpProp) ? dpProp.GetString() : null;

                            if (!string.IsNullOrEmpty(dPath) && (!rawUrl?.Contains("mmg") ?? true))
                                mediaUrl = $"https://mmg.whatsapp.net{dPath}";
                            else
                                mediaUrl = rawUrl;

                            mediaMime = aud.TryGetProperty("mimetype", out var mime) ? mime.GetString() : "audio/ogg; codecs=opus";
                            mediaKey = aud.TryGetProperty("mediaKey", out var mk) ? mk.GetString() : null;
                            fileSha256 = aud.TryGetProperty("fileSha256", out var fsh) ? fsh.GetString() : null;
                            fileEncSha256 = aud.TryGetProperty("fileEncSha256", out var feh) ? feh.GetString() : null;
                            directPath = dPath;

                            if (aud.TryGetProperty("fileLength", out var fl))
                                fileLength = fl.ValueKind == JsonValueKind.Number ? fl.GetInt64() : (long.TryParse(fl.GetString(), out var len) ? len : 0);

                            textPreview = "🎤 Nota de voz";
                            break;
                        }

                    case "videoMessage":
                        {
                            messageKind = MessageKind.Video; // Asegúrate de tener 'Video' en tu Enum MessageKind
                            mediaType = "video";

                            if (!message.TryGetProperty("videoMessage", out var vid)) return null;

                            // Extraer URL (Prioridad al DirectPath para evitar expiración)
                            var rawUrl = vid.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                            var dPath = vid.TryGetProperty("directPath", out var dpProp) ? dpProp.GetString() : null;

                            if (!string.IsNullOrEmpty(dPath))
                                mediaUrl = $"https://mmg.whatsapp.net{dPath}";
                            else
                                mediaUrl = rawUrl;

                            // Metadatos cruciales
                            mediaMime = vid.TryGetProperty("mimetype", out var mime) ? mime.GetString() : "video/mp4";
                            mediaKey = vid.TryGetProperty("mediaKey", out var mk) ? mk.GetString() : null;
                            fileSha256 = vid.TryGetProperty("fileSha256", out var fsh) ? fsh.GetString() : null;
                            fileEncSha256 = vid.TryGetProperty("fileEncSha256", out var feh) ? feh.GetString() : null;
                            directPath = dPath;

                            // Caption del video
                            mediaCaption = vid.TryGetProperty("caption", out var cap) ? cap.GetString() : null;

                            if (vid.TryGetProperty("fileLength", out var fl))
                                fileLength = fl.ValueKind == JsonValueKind.Number ? fl.GetInt64() : 0;

                            textPreview = !string.IsNullOrEmpty(mediaCaption) ? $"🎥 {mediaCaption}" : "🎥 Video";
                            text = mediaCaption;
                            break;
                        }

                    case "documentMessage":
                        {
                            messageKind = MessageKind.Document;
                            mediaType = "document";
                            var docu = message.GetProperty("documentMessage");
                            mediaUrl = docu.GetProperty("url").GetString();
                            mediaMime = docu.GetProperty("mimetype").GetString();
                            mediaCaption = docu.TryGetProperty("title", out var title) ? title.GetString() : null;
                            mediaKey = docu.TryGetProperty("mediaKey", out var mk) ? mk.GetString() : null;
                            fileSha256 = docu.TryGetProperty("fileSha256", out var fsh) ? fsh.GetString() : null;
                            fileEncSha256 = docu.TryGetProperty("fileEncSha256", out var feh) ? feh.GetString() : null;
                            directPath = docu.TryGetProperty("directPath", out var dp) ? dp.GetString() : null;
                            if (docu.TryGetProperty("mediaKeyTimestamp", out var mts))
                                mediaKeyTimestamp = ReadUnixTimestamp(mts);
                            fileName = docu.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                            if (docu.TryGetProperty("fileLength", out var fl))
                            {
                                if (fl.ValueKind == JsonValueKind.String && long.TryParse(fl.GetString(), out var len))
                                    fileLength = len;
                                else if (fl.ValueKind == JsonValueKind.Number)
                                    fileLength = fl.GetInt64();
                            }
                            pageCount = docu.TryGetProperty("pageCount", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : null;
                            thumbnailBase64 = docu.TryGetProperty("jpegThumbnail", out var jt) ? jt.GetString() : null;
                            textPreview = "[Documento]";
                            break;
                        }

                    case "stickerMessage":
                        {
                            messageKind = MessageKind.Sticker;
                            mediaType = "sticker";

                            // Intentamos obtener el nodo de diferentes formas por si Evolution cambia la estructura
                            JsonElement stk;
                            if (message.TryGetProperty("stickerMessage", out var s1)) stk = s1;
                            else stk = message; // Fallback si el nodo ya es el sticker

                            var rawUrl = stk.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                            var dPath = stk.TryGetProperty("directPath", out var dpProp) ? dpProp.GetString() : null;

                            //mediaUrl = stk.TryGetProperty("url", out var url) ? url.GetString() : null;
                            // Si la url apunta a web o está vacía, pero tenemos directPath
                            if ((string.IsNullOrEmpty(rawUrl) || rawUrl.Contains("web.whatsapp.net")) && !string.IsNullOrEmpty(dPath))
                            {
                                // El host estándar para archivos multimedia es mmg.whatsapp.net
                                mediaUrl = $"https://mmg.whatsapp.net{dPath}";
                            }
                            else
                            {
                                mediaUrl = rawUrl;
                            }

                            mediaMime = stk.TryGetProperty("mimetype", out var mime) ? mime.GetString() : "image/webp";
                            mediaKey = stk.TryGetProperty("mediaKey", out var mk) ? mk.GetString() : null;
                            fileSha256 = stk.TryGetProperty("fileSha256", out var fsh) ? fsh.GetString() : null;
                            fileEncSha256 = stk.TryGetProperty("fileEncSha256", out var feh) ? feh.GetString() : null;
                            directPath = stk.TryGetProperty("directPath", out var dp) ? dp.GetString() : null;
                            if (stk.TryGetProperty("mediaKeyTimestamp", out var mts))
                            {
                                if (mts.ValueKind == JsonValueKind.String && long.TryParse(mts.GetString(), out var ts))
                                    mediaKeyTimestamp = ts;
                                else if (mts.ValueKind == JsonValueKind.Number)
                                    mediaKeyTimestamp = mts.GetInt64();
                            }
                            if (stk.TryGetProperty("fileLength", out var fl))
                            {
                                if (fl.ValueKind == JsonValueKind.String && long.TryParse(fl.GetString(), out var len))
                                    fileLength = len;
                                else if (fl.ValueKind == JsonValueKind.Number)
                                    fileLength = fl.GetInt64();
                            }
                            mediaCaption = null;
                            textPreview = "[Sticker]";
                            break;
                        }

                    case "reactionMessage":
                        {
                            messageKind = MessageKind.Text; // O puedes crear MessageKind.Reaction si prefieres
                            var reaction = message.GetProperty("reactionMessage");

                            // El emoji enviado
                            text = reaction.TryGetProperty("text", out var emoji) ? emoji.GetString() : "";

                            // ID del mensaje al que se reacciona (útil para mostrarlo en la burbuja correcta)
                            var targetMessageId = reaction.GetProperty("key").GetProperty("id").GetString();

                            textPreview = $"Reaccionó {text} a un mensaje";

                            // Opcional: Puedes guardar el ID del mensaje original en una columna de "Notas" 
                            // o "ReplyTo" si tu tabla lo permite.
                            break;
                        }

                    case "editedMessage":
                        {
                            if (message.TryGetProperty("editedMessage", out var edElem) &&
                                edElem.TryGetProperty("message", out var innerMsg) &&
                                innerMsg.TryGetProperty("protocolMessage", out var protocolMsg))
                            {
                                // 1. Extraer el ID del mensaje original
                                if (protocolMsg.TryGetProperty("key", out var oldKey))
                                {
                                    externalMessageId = oldKey.GetProperty("id").GetString();
                                }

                                // 2. Extraer el nuevo texto
                                if (protocolMsg.TryGetProperty("editedMessage", out var innerEdited))
                                {
                                    if (innerEdited.TryGetProperty("conversation", out var conv))
                                    {
                                        text = conv.GetString();
                                    }
                                    else if (innerEdited.TryGetProperty("extendedTextMessage", out var ext) &&
                                             ext.TryGetProperty("text", out var tEdit)) // Cambiamos t por tEdit
                                    {
                                        text = tEdit.GetString();
                                    }
                                }

                                messageKind = MessageKind.Text;
                                textPreview = $"✎ {text ?? ""}";
                            }
                            break;
                        }

                    default:
                        messageKind = MessageKind.Text;
                        textPreview = "[Mensaje]";
                        break;
                }

                // =========================
                // Construcción del snapshot
                // =========================
                return new EvolutionMessageSnapshotDto
                {
                    ThreadId = threadId,
                    BusinessAccountId = instance,

                    Sender = (fromMe && !string.IsNullOrEmpty(senderPn)) ? senderPn : (senderRoot ?? remoteJid),
                    CustomerPhone = finalPhone, //  Número real extraído
                    CustomerLid = finalLid,
                    CustomerDisplayName = computedDisplayName, //  Nombre inteligente
                    DirectionIn = !fromMe,
                    MessageKind = messageKind,
                    MessageType = messageType,
                    Text = text,
                    TextPreview = textPreview,
                    MediaUrl = mediaUrl,
                    MediaMime = mediaMime,
                    MediaCaption = mediaCaption,
                    MediaType = mediaType,

                    MediaKey = mediaKey,
                    FileSha256 = fileSha256,
                    FileEncSha256 = fileEncSha256,
                    DirectPath = directPath,
                    MediaKeyTimestamp = mediaKeyTimestamp,
                    FileName = fileName,
                    FileLength = fileLength,
                    PageCount = pageCount,
                    ThumbnailBase64 = thumbnailBase64,

                    ExternalMessageId = externalMessageId,
                    ExternalTimestamp = timestamp,
                    Source = source,
                    RawPayloadJson = rawBody,
                    CreatedAtUtc = createdLocal,

                    QuotedMessageId = quotedId,
                    QuotedMessageText = quotedText

                };
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BuildSnapshot failed");
                return null;
            }
        }


        // --- HELPERS DE SEGURIDAD Y FORMATO ---
        public static string ComputeHmacSha256(string secret, string payload)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeSha256(string raw)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }

        public static bool FixedTimeEqualsHex(string aHex, string bHex)
        {
            try
            {
                var a = Convert.FromHexString(aHex);
                var b = Convert.FromHexString(bHex.ToString());
                return CryptographicOperations.FixedTimeEquals(a, b);
            }
            catch
            {
                return false;
            }
        }

        public static long ReadUnixTimestamp(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.GetInt64();

            if (element.ValueKind == JsonValueKind.String &&
                long.TryParse(element.GetString(), out var value))
                return value;

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }


    }
}
