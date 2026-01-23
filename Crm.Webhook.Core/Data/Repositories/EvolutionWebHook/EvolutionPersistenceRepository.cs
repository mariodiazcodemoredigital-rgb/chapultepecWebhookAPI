using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using Crm.Webhook.Core.Parsers;
using Crm.Webhook.Core.Services.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Data.Repositories.EvolutionWebHook
{
    public class EvolutionPersistenceRepository
    {
        private readonly IDbContextFactory<CrmInboxDbContext> _factory;

        private readonly IHubContext<CrmHub> _hubContext;

        public EvolutionPersistenceRepository(IDbContextFactory<CrmInboxDbContext> factory, IHubContext<CrmHub> hubContext)
        {
            _factory = factory;
            _hubContext = hubContext;
        }

        public async Task<int> SaveRawPayloadAsync(string rawBody, string? remoteIp, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            var data = root.TryGetProperty("data", out var d) ? d : root;

            string? instance = root.TryGetProperty("instance", out var i) ? i.GetString() : null;
            string? @event = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            string? sender = root.TryGetProperty("sender", out var s) ? s.GetString() : null;
            string? messageType = data.TryGetProperty("messageType", out var mt) ? mt.GetString() : null;
            string? pushName = data.TryGetProperty("pushName", out var pn) ? pn.GetString() : null;

            bool? fromMe = null;
            string? remoteJid = null;
            string? senderPn = null; //  Nueva variable para el número real

            if (data.TryGetProperty("key", out var key))
            {
                if (key.TryGetProperty("fromMe", out var fm))
                    fromMe = fm.GetBoolean();

                if (key.TryGetProperty("remoteJid", out var rj))
                    remoteJid = rj.GetString();

                //  Extraemos también el senderPn si existe en la key
                if (key.TryGetProperty("senderPn", out var spn))
                    senderPn = spn.GetString();
            }

            // --- LÓGICA DE UNIFICACIÓN Y LIMPIEZA ---
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
                    finalLid = remoteJid;
                    // Intentar rescatar el teléfono si viene en senderPn
                    if (senderPn != null && senderPn.Contains("@s.whatsapp.net"))
                    {
                        finalPhone = senderPn.Replace("@s.whatsapp.net", "");
                    }
                }
            }

            TimeZoneInfo mexicoZone;
            try
            {
                mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)");
            }
            catch
            {
                mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
            }

            // 1. Declaramos la variable con un valor por defecto (por si el JSON no trae fecha)
            DateTime messageDate = DateTime.UtcNow;
            DateTime receivedLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);

            if (data.TryGetProperty("messageTimestamp", out var tsElement))
            {
                // 1. Obtener el timestamp del elemento
                var timestamp = EvolutionParser.ReadUnixTimestamp(tsElement);

                // 2. Convertir el Unix Timestamp a un DateTimeOffset en UTC
                var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(timestamp);

                // 3. Definir la zona horaria de México (ajusta el ID según tu región si no es CDMX)
                // En Windows es "Central Standard Time (Mexico)", en Linux/Docker suele ser "America/Mexico_City"

                // 4. Convertir a la hora local de esa zona
                messageDate = TimeZoneInfo.ConvertTimeFromUtc(dateTimeOffset.UtcDateTime, mexicoZone);
            }

            //  El ThreadId de auditoría debe ser consistente: wa:numero
            // Antes tenías instance:remoteJid, pero es mejor wa:phone para ligarlo fácil
            // El ThreadId de auditoría ahora sigue la misma regla: wa:tel o wa:lid:id
            string auditThreadId = !string.IsNullOrEmpty(finalPhone)
                ? $"wa:{finalPhone}"
                : (finalLid != null ? $"wa:lid:{finalLid.Replace("@lid", "")}" : "wa:unknown");

            var entity = new EvolutionRawPayload
            {
                ThreadId = auditThreadId,
                Source = "evolution",
                PayloadJson = rawBody,
                ReceivedUtc = receivedLocal,
                Processed = false,

                Instance = instance,
                Event = @event,
                MessageType = messageType,
                RemoteJid = remoteJid, // Guardamos el original (@lid si es el caso)
                FromMe = fromMe,
                Sender = sender,
                CustomerPhone = finalPhone, // Guardamos el número limpio
                CustomerDisplayName = pushName,
                MessageDateUtc = messageDate,

                Notes = remoteIp
            };

         
            await using var db = await _factory.CreateDbContextAsync(ct);

            db.EvolutionRawPayloads.Add(entity);
            await db.SaveChangesAsync(ct);
            //var raw = await db.EvolutionRawPayloads.FirstOrDefaultAsync(x => x.ExternalId == snap.ExternalMessageId);
            //if (raw != null)
            //{
            //    raw.Processed = true;
            //    await db.SaveChangesAsync();
            //}


            return entity.Id;
        }

        //Guardado cuando esta incorrecto el payloadraw
        public async Task SaveRawEvolutionPayloadAsync(string rawBody, string threadId, CancellationToken ct)
        {           
            await using var db = await _factory.CreateDbContextAsync(ct);

            db.EvolutionRawPayloads.Add(new EvolutionRawPayload
            {
                ThreadId = threadId,
                PayloadJson = rawBody,
                ReceivedUtc = DateTime.UtcNow,
                Source = "evolution",
                Processed = false
            });

            await db.SaveChangesAsync(ct);
        }

        //Ya con estos guarda la informacion en las tablas de thread y messages (OBTIENE Y/O CREA EL THREAD)
        public async Task<CrmThread> GetOrCreateThreadAsync(EvolutionMessageSnapshotDto snap, CancellationToken ct = default)
        {          
            await using var db = await _factory.CreateDbContextAsync(ct);

            // 1. BUSQUEDA INICIAL
            // Buscamos por Teléfono (El principal)
            var mainThread = !string.IsNullOrEmpty(snap.CustomerPhone)
                ? await db.CrmThreads.FirstOrDefaultAsync(t => t.CustomerPhone == snap.CustomerPhone, ct)
                : null;

            // Buscamos por LID (El temporal)
            var lidThread = !string.IsNullOrEmpty(snap.CustomerLid)
                ? await db.CrmThreads.FirstOrDefaultAsync(t => t.CustomerLid == snap.CustomerLid, ct)
                : null;

            // --- ESCENARIO 1: FUSIÓN (MERGE) ---
            // Si existen ambos y son distintos, migramos todo al de Teléfono (Main)
            if (mainThread != null && lidThread != null && mainThread.Id != lidThread.Id)
            {
                //_log.LogInformation("FUSIONANDO: Thread LID {lidId} se mueve a Main {mainId}", lidThread.Id, mainThread.Id);

                mainThread.CustomerLid = snap.CustomerLid;

                var messagesToMigrate = await db.CrmMessages
                    .Where(m => m.ThreadRefId == lidThread.Id)
                    .ToListAsync(ct);

                foreach (var m in messagesToMigrate)
                {
                    m.ThreadRefId = mainThread.Id;
                }

                db.CrmThreads.Remove(lidThread);

                // Actualizamos meta-datos del principal antes de guardar
                mainThread.LastMessageUtc = snap.CreatedAtUtc;
                mainThread.LastMessagePreview = snap.TextPreview;
                if (snap.DirectionIn) mainThread.UnreadCount += 1;

                await db.SaveChangesAsync(ct);
                return mainThread;
            }

            // --- ESCENARIO 2: ACTUALIZACIÓN (Tu lógica que ya funciona) ---
            // Si llegamos aquí, o solo hay uno, o no hay ninguno.
            var existingThread = mainThread ?? lidThread;

            if (existingThread != null)
            {
                // Conservamos tu lógica de validación de nombres genéricos
                bool isGenericName = existingThread.CustomerDisplayName == "Contacto LID (Pendiente)" ||
                                     existingThread.CustomerDisplayName == "Contacto LID" ||
                                     existingThread.CustomerDisplayName == "Prospecto de Anuncio";

                if (isGenericName && !string.IsNullOrEmpty(snap.CustomerDisplayName))
                {
                    existingThread.CustomerDisplayName = snap.CustomerDisplayName;
                }

                // Aseguramos que el LID se guarde si el thread no lo tenía
                if (string.IsNullOrEmpty(existingThread.CustomerLid) && !string.IsNullOrEmpty(snap.CustomerLid))
                {
                    existingThread.CustomerLid = snap.CustomerLid;
                }

                // Tu lógica de "Promoción" de LID a Teléfono Real
                if (string.IsNullOrEmpty(existingThread.CustomerPhone) && !string.IsNullOrEmpty(snap.CustomerPhone))
                {
                    existingThread.ThreadId = snap.ThreadId;
                    existingThread.CustomerPhone = snap.CustomerPhone;
                    existingThread.ThreadKey = snap.CustomerPhone;
                    existingThread.MainParticipant = snap.CustomerPhone;
                    existingThread.CustomerPlatformId = snap.CustomerPhone + "@s.whatsapp.net";

                    if (!string.IsNullOrEmpty(snap.CustomerDisplayName))
                    {
                        existingThread.CustomerDisplayName = snap.CustomerDisplayName;
                    }
                }

                existingThread.LastMessageUtc = snap.CreatedAtUtc;
                existingThread.LastMessagePreview = snap.TextPreview;
                if (snap.DirectionIn) existingThread.UnreadCount += 1;

                await db.SaveChangesAsync(ct);
                return existingThread;
            }

            // --- ESCENARIO 3: CREACIÓN DE HILO NUEVO ---
            var thread = new CrmThread
            {
                ThreadId = snap.ThreadId,
                BusinessAccountId = snap.BusinessAccountId,
                Channel = 1,
                ThreadKey = !string.IsNullOrEmpty(snap.CustomerPhone) ? snap.CustomerPhone : snap.CustomerLid,
                MainParticipant = !string.IsNullOrEmpty(snap.CustomerPhone) ? snap.CustomerPhone : snap.CustomerLid,
                CustomerDisplayName = snap.CustomerDisplayName,
                CustomerPhone = snap.CustomerPhone,
                CustomerLid = snap.CustomerLid,
                CustomerPlatformId = !string.IsNullOrEmpty(snap.CustomerPhone)
                                     ? snap.CustomerPhone + "@s.whatsapp.net"
                                     : snap.CustomerLid,
                CreatedUtc = snap.CreatedAtUtc,
                LastMessageUtc = snap.CreatedAtUtc,
                LastMessagePreview = snap.TextPreview,
                UnreadCount = snap.DirectionIn ? 1 : 0,
                Status = 0
            };

            db.CrmThreads.Add(thread);
            await db.SaveChangesAsync(ct);

            return thread;
        }


        // Valida que el mensaje no exista en la tabla, Regresa el mensaje si existe
        public async Task<bool> MessageExistsAsync(EvolutionMessageSnapshotDto snap, int threadId, CancellationToken ct = default)
        {          
            await using var db = await _factory.CreateDbContextAsync(ct);

            if (!string.IsNullOrEmpty(snap.ExternalMessageId))
            {
                return await db.CrmMessages.AnyAsync(m =>
                    m.ThreadRefId == threadId &&
                    m.ExternalId == snap.ExternalMessageId, ct);
            }

            var hash = EvolutionParser.ComputeSha256(snap.RawPayloadJson);

            return await db.CrmMessages.AnyAsync(m =>
                m.ThreadRefId == threadId &&
                m.RawHash == hash, ct);
        }

        //Inserta Valores  Media en la tabla (CrmMessagesMedias)
        public async Task InsertMediaAsync(CrmMessage msg, EvolutionMessageSnapshotDto s, CancellationToken ct = default)
        {           
            await using var db = await _factory.CreateDbContextAsync(ct);

            if (!msg.HasMedia) return;

            // 1. Obtener la zona horaria de México (puedes mover esto al inicio del método)
            TimeZoneInfo mexicoZone;
            try
            {
                mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)");
            }
            catch
            {
                mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
            }

            // 2. Convertir la hora actual (NOW) a hora de México
            var nowMexico = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);

            var media = new CrmMessageMedia
            {
                MessageId = msg.Id,
                MediaType = s.MessageType,
                MimeType = s.MediaMime,
                MediaUrl = s.MediaUrl,
                MediaKey = s.MediaKey,
                FileSha256 = s.FileSha256,
                FileEncSha256 = s.FileEncSha256,
                DirectPath = s.DirectPath,
                MediaKeyTimestamp = s.MediaKeyTimestamp,
                FileName = s.FileName,
                FileLength = s.FileLength,
                PageCount = s.PageCount,
                ThumbnailBase64 = s.ThumbnailBase64,
                CreatedUtc = nowMexico
            };

            db.CrmMessageMedias.Add(media);
            await db.SaveChangesAsync();
        }

        private async Task NotifySignalRAsync(CrmThread thread, CrmMessage msg)
        {
            if (string.IsNullOrEmpty(thread.BusinessAccountId))
                return;

            // Si en el log ves que llega vacío o diferente, ahí está el error.
            var grupoDestino = thread.BusinessAccountId ?? "ChapultepecEvo";

            await _hubContext.Clients
                .Group(grupoDestino)
                .SendAsync("NewMessage", new
                {

                    ThreadId = thread.ThreadId,
                    ThreadDbId = thread.Id,
                    MessageId = msg.Id,
                    Sender = msg.Sender,
                    DisplayName = msg.DisplayName,
                    Text = msg.Text,
                    MessageKind = msg.MessageKind,
                    MediaUrl = msg.MediaUrl,
                    CreatedUtc = msg.TimestampUtc,
                    DirectionIn = msg.DirectionIn
                });
        }

        //Guarda en la tabla  Mensajes (CrmMessages), manda a llamar si tiene Medias (inserta medias) /Notifica SignalR
        public async Task PersistSnapshotAsync(EvolutionMessageSnapshotDto snap, CancellationToken ct = default)
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            var thread = await GetOrCreateThreadAsync(snap, ct);

            // 1. Obtener la zona horaria de México (puedes mover esto al inicio del método)
            TimeZoneInfo mexicoZone;
            try
            {
                mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)");
            }
            catch
            {
                mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
            }

            // 2. Convertir la hora actual (NOW) a hora de México
            var nowMexico = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);

            // LÓGICA DE REACCIONES A LOS MENSAJES
            if (snap.MessageType == "reactionMessage")
            {
                try
                {
                    using var doc = JsonDocument.Parse(snap.RawPayloadJson);
                    var root = doc.RootElement;

                    // El JSON de Evolution tiene el nodo "data" en la raíz
                    if (!root.TryGetProperty("data", out var dataNode)) dataNode = root;

                    if (dataNode.TryGetProperty("message", out var messageNode) &&
                        messageNode.TryGetProperty("reactionMessage", out var reactionNode))
                    {
                        // 1. Extraer el ID del mensaje original (al que se reacciona)
                        var targetExternalId = reactionNode.GetProperty("key").GetProperty("id").GetString();

                        // 2. Obtenemos el emoji
                        var emoji = reactionNode.TryGetProperty("text", out var textProp) ? textProp.GetString() : "";

                        //_log.LogInformation("[Reaction] Intentando aplicar {emoji} al mensaje {id}", emoji, targetExternalId);

                        // 3. Buscar el mensaje original en la base de datos
                        // IMPORTANTE: Asegúrate que thread.Id sea el correcto
                        var originalMessage = await db.CrmMessages
                            .FirstOrDefaultAsync(m => m.ExternalId == targetExternalId && m.ThreadRefId == thread.Id, ct);

                        if (originalMessage != null)
                        {
                            // 4. Actualizar la reacción
                            originalMessage.Reaction = string.IsNullOrEmpty(emoji) ? null : emoji;

                            // 5. Actualizar el thread para el preview
                            thread.LastMessageUtc = DateTime.UtcNow;
                            thread.LastMessagePreview = string.IsNullOrEmpty(emoji) ? "Quitó una reacción" : $"Reaccionó {emoji}";

                            await db.SaveChangesAsync(ct);

                            // 6. Notificar a SignalR
                            // "ChapultepecEvo" es el nombre del grupo al que se unieron los agentes
                            //await _hubContext.Clients.Group("ChapultepecEvo")
                            //    .SendAsync("NewMessage", new
                            //    {
                            //        threadId = thread.ThreadId,
                            //        externalId = targetExternalId,
                            //        reaction = emoji
                            //    });
                            //_log.LogInformation("[Reaction] Reacción aplicada con éxito.");
                        }
                        else
                        {
                            //_log.LogWarning("[Reaction] No se encontró el mensaje original {id} en el thread {t}", targetExternalId, thread.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    //_log.LogError(ex, "Error procesando reactionMessage");
                }

                return; // Salimos para no insertar un mensaje nuevo de tipo reacción
            }

            // LÓGICA PARA MENSAJES EDITADOS
            if (snap.MessageType == "editedMessage")
            {
                try
                {
                    // Buscamos el mensaje original usando el ExternalId que rescatamos en BuildSnapshot
                    var originalMessage = await db.CrmMessages
                        .FirstOrDefaultAsync(m => m.ExternalId == snap.ExternalMessageId && m.ThreadRefId == thread.Id, ct);

                    if (originalMessage != null)
                    {

                        originalMessage.Text = snap.Text;
                        originalMessage.UpdatedAt = nowMexico; // El campo que agregamos

                        // Actualizamos el preview del hilo
                        thread.LastMessageUtc = snap.CreatedAtUtc;
                        thread.LastMessagePreview = snap.TextPreview;

                        await db.SaveChangesAsync(ct);

                        // NOTIFICAR A SIGNALR: Reutilizamos NotifySignalRAsync o mandamos un evento específico
                        // Es mejor mandar un evento que la UI sepa que es una edición
                        //await _hubContext.Clients.Group("ChapultepecEvo").SendAsync("MessageUpdated", new
                        //{
                        //    ThreadId = thread.ThreadId,
                        //    ExternalId = snap.ExternalMessageId,
                        //    NewText = snap.Text
                        //});

                        //_log.LogInformation("[Edit] Mensaje {id} actualizado vía Webhook", snap.ExternalMessageId);
                    }
                }
                catch (Exception ex)
                {
                    //_log.LogError(ex, "Error procesando editedMessage");
                }
                return; // Importante: Salimos para no intentar insertar un mensaje nuevo
            }


            if (await MessageExistsAsync(snap, thread.Id, ct))
                return;

            var rawHash = EvolutionParser.ComputeSha256(snap.RawPayloadJson);

            var message = new CrmMessage
            {
                ThreadRefId = thread.Id,
                Sender = snap.Sender,
                DisplayName = snap.CustomerDisplayName,
                Text = snap.Text,
                TimestampUtc = snap.CreatedAtUtc,
                ExternalTimestamp = snap.ExternalTimestamp,
                DirectionIn = snap.DirectionIn,

                MediaUrl = snap.MediaUrl,
                MediaMime = snap.MediaMime,
                MediaCaption = snap.MediaCaption,
                MediaType = snap.MessageType,

                RawPayload = snap.RawPayloadJson,
                ExternalId = snap.ExternalMessageId,
                WaMessageId = snap.ExternalMessageId,
                RawHash = rawHash,

                MessageKind = (int)snap.MessageKind,
                HasMedia = snap.MediaUrl != null,

                CreatedUtc = nowMexico,
                UpdatedAt = snap.CreatedAtUtc,

                QuotedMessageId = snap.QuotedMessageId,
                QuotedMessageText = snap.QuotedMessageText
            };

            db.CrmMessages.Add(message);

            // =========================
            // Update Thread state
            // =========================
            thread.LastMessageUtc = snap.CreatedAtUtc;
            thread.LastMessagePreview = snap.TextPreview;

            if (snap.DirectionIn)
                thread.UnreadCount += 1;

            try
            {
                await db.SaveChangesAsync(ct);
                //_log.LogInformation("✅ Guardado exitoso en DB");
            }
            catch (DbUpdateException ex)
            {
                // AQUÍ verás si es un problema de nombres de columna o de llaves foráneas
                //_log.LogError(ex.InnerException?.Message ?? ex.Message);
                throw;
            }

            //var msg = await InsertMessageAsync(thread, snap, ct);
            await InsertMediaAsync(message, snap, ct);

            // Notifica SignalR
            await NotifySignalRAsync(thread, message);
        }
    }
}
