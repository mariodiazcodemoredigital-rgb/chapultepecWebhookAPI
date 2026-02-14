using Crm.Webhook.Core.Data;
using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using Crm.Webhook.Core.Parsers;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace Crm.Webhook.Api.Controllers
{
    [Route("api/webhook/evolution")]
    [ApiController]
    public class EvolutionWebhookController : ControllerBase
    {
        private readonly ILogger<EvolutionWebhookController> _log;
        private readonly IMessageQueue _queue;
        private readonly IDbContextFactory<CrmInboxDbContext> _dbFactory;
        private readonly EvolutionParser _parser;
        private readonly IEvolutionPersistenceService _persistence;
        private readonly IConfiguration _cfg;
        private readonly string? _inboundToken;
        private readonly string? _hmacSecret;        
        private readonly string[] _ipWhitelist;



        public EvolutionWebhookController(
            ILogger<EvolutionWebhookController> log,
            IMessageQueue queue,
            IDbContextFactory<CrmInboxDbContext> dbFactory,
            EvolutionParser parser,
            IEvolutionPersistenceService persistence,
            IConfiguration cfg)
        {
            _log = log;
            _queue = queue;
            _dbFactory = dbFactory;
            _parser = parser;
            _persistence = persistence;
            _cfg = cfg;
            _hmacSecret = cfg["Evolution:WebhookHmacSecret"];        // opcional
            _inboundToken = cfg["Evolution:WebhookInboundToken"];
            _ipWhitelist = (cfg["Evolution:WebhookIpWhitelist"] ?? "")
                           .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromServices] IWebhookControlService toggle, CancellationToken ct)
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

            // 1. LOG DE ENTRADA INICIAL
            _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] POST | 📩 Webhook recibido desde IP: {RemoteIp}", remoteIp);

            // Verificación del Switch General
            if (!await toggle.IsEvolutionEnabledAsync(ct))
            {
                _log.LogWarning("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER][IsEvolutionEnabledAsync].[Post] ADVERTENCIA | Webhook recibido pero DESACTIVADO en BD.");                
                return Ok(new { status = "disabled" });
            }

            /// 1) Validar IP (opcional y complementario si tienes ips de Evolution)
            if (_ipWhitelist.Length > 0)
            {
                
                if (string.IsNullOrEmpty(remoteIp) || !_ipWhitelist.Contains(remoteIp))
                {
                    _log.LogWarning("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ERROR | IP {Ip} no está en lista blanca.", remoteIp);
                    return Unauthorized();
                }
            }

            // 2) Leer body raw (necesario para HMAC)
            Request.EnableBuffering(); // permite re-leer stream
            using var sr = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await sr.ReadToEndAsync();
            Request.Body.Position = 0;

            // 3) Validar token (fallback)
            if (!string.IsNullOrEmpty(_inboundToken))
            {
                if (!Request.Headers.TryGetValue("X-Webhook-Token", out var tokenHeader) ||
                    tokenHeader != _inboundToken)
                {
                    _log.LogWarning("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ERROR | Token de entrada inválido.");                    
                    return Unauthorized();
                }
            }

            // 4) Validar HMAC (si está configurado)
            if (!string.IsNullOrEmpty(_hmacSecret))
            {
                if (!Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
                {
                    _log.LogWarning("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ERROR | Token de entrada inválido.");
                    return Unauthorized();
                }
                var computed = EvolutionParser.ComputeHmacSha256(_hmacSecret, body);
                if (!EvolutionParser.FixedTimeEqualsHex(computed, signatureHeader))
                {
                    _log.LogWarning("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ERROR | Token de entrada inválido.");                    
                    return Unauthorized();
                }
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Obtener el tipo de evento
            string? eventType = root.TryGetProperty("event", out var ev) ? ev.GetString() : null;

            // FILTRO DE RUIDO: Ignorar eventos que llenan la tabla de basura
            var eventosIgnorados = new[] { "chats.update", "presence.update" };

            if (eventosIgnorados.Contains(eventType))
            {
                // Respondemos 200 para que Evolution no reintente, pero NO guardamos nada
                return Ok(new { status = "ignored_noise" });
            }

            _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] PROCESO | Evento detectado: {Event}", eventType);

            // ============================================================
            // BLOQUE 1: PROCESAMIENTO DE STATUS (CHECKS)
            // ============================================================
            if (eventType == "messages.update" || eventType == "message-update.set")
            {
                _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] PROCESO | Actualizando status de mensaje...");

                var data = root.GetProperty("data");
                string? externalId = null;

                // Buscamos el ID del mensaje (soporta ambos formatos de Evolution)
                if (data.TryGetProperty("keyId", out var kid)) externalId = kid.GetString();
                else if (data.TryGetProperty("key", out var k) && k.TryGetProperty("id", out var id)) externalId = id.GetString();

                if (!string.IsNullOrEmpty(externalId) && data.TryGetProperty("status", out var sProp))
                {
                    string statusStr = sProp.ValueKind == JsonValueKind.String ? sProp.GetString()?.ToUpper() : "";
                    int ackNum = sProp.ValueKind == JsonValueKind.Number ? sProp.GetInt32() : -1;

                    // Mapeo jerárquico: 0=Enviado, 1=Entregado, 2=Leído
                    int statusValue = -1;
                    if (!string.IsNullOrEmpty(statusStr))
                    {
                        statusValue = statusStr switch
                        {
                            "SERVER_ACK" => 0,
                            "DELIVERY_ACK" or "DELIVERED" => 1,
                            "READ" or "PLAYED" => 2,
                            _ => -1
                        };
                    }
                    else
                    {
                        statusValue = ackNum switch { 2 => 0, 3 => 1, 4 => 2, _ => -1 };
                    }

                    if (statusValue != -1)
                    {
                        using var scope = HttpContext.RequestServices.CreateScope();
                        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CrmInboxDbContext>>();
                        await using var db = await dbFactory.CreateDbContextAsync(ct);

                        var msg = await db.CrmMessages.FirstOrDefaultAsync(m => m.ExternalId == externalId, ct);
                        if (msg != null)
                        {
                            // PROTECCIÓN: Solo subir de nivel, nunca bajar (evita que un Delivery pise un Read)
                            if (msg.Status < statusValue)
                            {
                                msg.Status = statusValue;
                                await db.SaveChangesAsync(ct);
                            }                           
                        }
                    }
                }

                // Solo añadimos un log al final del bloque si fue exitoso
                _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ÉXITO | Status de mensaje sincronizado.");

                // Si el evento era SOLO un update de status, terminamos aquí para no crear duplicados
                if (eventType != "messages.upsert") return Ok(new { status = "status_sync_ok" });
            }

            // Si el código llega aquí, continuará con el guardado normal del mensaje...

            if (eventType == "contacts.update")
            {
                try
                {
                    _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] PROCESO | Actualizando perfil de contacto...");

                    var dataElement = root.GetProperty("data");
                    JsonElement contact = (dataElement.ValueKind == JsonValueKind.Array && dataElement.GetArrayLength() > 0)
                                          ? dataElement[0] : dataElement;

                    string remoteJid = contact.GetProperty("remoteJid").GetString() ?? ""; // Ej: 521... @s.whatsapp.net
                    string? pushName = contact.TryGetProperty("pushName", out var p) ? p.GetString() : null;
                    string? profilePic = contact.TryGetProperty("profilePicUrl", out var img) ? img.GetString() : null;

                    if (!string.IsNullOrEmpty(remoteJid))
                    {
                        // Extraemos solo el número o el ID base del JID de WhatsApp
                        // Esto convierte "521... @s.whatsapp.net" en "521..."
                        string rawIdprofile = remoteJid.Split('@')[0];

                        using var scope = HttpContext.RequestServices.CreateScope();
                        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CrmInboxDbContext>>();
                        await using var db = await dbFactory.CreateDbContextAsync(ct);

                        // BÚSQUEDA INTELIGENTE:
                        // Buscamos cualquier ThreadId que CONTENGA el número base o el ID que llegó.
                        // Esto cubrirá: "wa:521...", "ChapultepecEvo:521...", "wa:lid:..." etc.
                        var thread = await db.CrmThreads.FirstOrDefaultAsync(t =>
                            t.ThreadId.Contains(rawIdprofile) ||
                            t.CustomerLid == remoteJid ||
                            t.ThreadId == remoteJid, ct);

                        if (thread != null)
                        {
                            bool huboCambios = false;

                            if (!string.IsNullOrEmpty(pushName) && thread.CustomerDisplayName != pushName)
                            {
                                thread.CustomerDisplayName = pushName;
                                huboCambios = true;
                            }

                            if (!string.IsNullOrEmpty(profilePic) && thread.CustomerPhotoUrl != profilePic)
                            {
                                thread.CustomerPhotoUrl = profilePic;
                                huboCambios = true;
                            }

                            if (huboCambios)
                            {
                                await db.SaveChangesAsync(ct);
                            }
                        }
                    }
                    _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ÉXITO | Perfil de contacto actualizado.");
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ERROR | Fallo en contacts.update");                    
                }
                return Ok(new { status = "profile_processed" });
            }

            // Guardar RAW PAYLOAD (SIEMPRE) POR AUDITORIA
            var rawId = await _persistence.SaveRawPayloadAsync(body, remoteIp, ct);

            _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] DATABASE | Raw Payload guardado. ID: {RawId}", rawId);

            // Paso Intermedio , mapear envelope
            var envelope = _parser.MapEvolutionToEnvelope(body);

            if (envelope == null)
            {
                _log.LogWarning("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ADVERTENCIA | Payload inválido, no se pudo crear envelope.");                
                await _persistence.SaveRawEvolutionPayloadAsync(body, "unknown", ct);
                return Ok(new { status = "accepted_raw" });
            }

            // CRÍTICO: Si el mensaje NO es entrante (DirectionIn es false significa que salió de nosotros)
            // simplemente lo ignoramos para no duplicar lo que el agente ya escribió.
            if (envelope.DirectionIn == false)
            {
                _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] INFO | Mensaje saliente ignorado para evitar duplicados.");                
                return Ok(new { status = "ignored_outbound" });
            }
                        

            // 5) Reutilizar el envelope ya mapeado anteriormente
            // No necesitamos llamar a MapEvolutionToIncoming()
            if (envelope == null)
            {
                _log.LogWarning("No hay un envelope válido para encolar.");
                return Ok(new { status = "accepted_raw" });
            }

            // Convertimos el envelope a IncomingMessageDto (el objeto que entiende tu cola)
            // Usamos tu constructor de IncomingMessageDto con los datos ya procesados del envelope
            var incoming = new IncomingMessageDto(
                 threadId: envelope.ThreadId,
                 businessAccountId: envelope.BusinessAccountId,
                 sender: envelope.CustomerPhone ?? "unknown",
                 displayName: envelope.CustomerDisplayName ?? "",
                 text: envelope.Text ?? "",
                 timestamp: envelope.ExternalTimestamp,
                 directionIn: envelope.DirectionIn, // Aquí usamos el valor calculado (!fromMe)
                 ai: null,
                 action: "initial",
                 reason: "incoming_from_evolution",
                 title: (envelope.CustomerDisplayName ?? envelope.CustomerPhone)?.Split(' ').FirstOrDefault() ?? "Nuevo",
                 RawPayload: body
            );

            // 6) ENCOLAR
            //    ACK rápido: contestar antes de processamento pesado
            //    Encolar procesamiento y retornar 200 Accepted (o 200 OK)
            try
            {
                _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] PROCESO | Encolando mensaje para: {ThreadId}", envelope.ThreadId);
                await _queue.EnqueueAsync(incoming);
                _log.LogInformation("[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] ÉXITO | Webhook aceptado y encolado correctamente.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[WEBHOOKAPI].[EVOLUTIONWEBHOOKCONTROLLER].[Post] EXCEPCIÓN CRÍTICA | Fallo al encolar mensaje.");                
                // 500 for queue problems
                return StatusCode(500, "enqueue_failed");
            }

            // Devolver 200 lo antes posible: Evolution espera status 200/2xx
            return Ok(new { status = "accepted" });
        }
    }
    
}
