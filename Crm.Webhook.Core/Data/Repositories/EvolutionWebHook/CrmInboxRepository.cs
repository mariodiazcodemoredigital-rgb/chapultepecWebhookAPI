using Azure;
using Crm.Webhook.Core.Dtos;
using Crm.Webhook.Core.Models;
using Crm.Webhook.Core.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static Crm.Webhook.Core.Dtos.EvolutionSendDto;

namespace Crm.Webhook.Core.Data.Repositories.EvolutionWebHook
{
    public class CrmInboxRepository
    {
        private readonly IDbContextFactory<CrmInboxDbContext> _dbFactory;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _cfg;
        private readonly ILogger<CrmInboxRepository> _logger;

        public CrmInboxRepository(IDbContextFactory<CrmInboxDbContext> dbFactory,
                                HttpClient httpClient,
                                IConfiguration cfg,
                                ILogger<CrmInboxRepository> logger)
        {
            _dbFactory = dbFactory;
            _httpClient = httpClient;
            _cfg = cfg;
            _logger = logger;
        }

        // Usuario actual simulado (o inyectado si tienes Auth)
        public string CurrentUser { get; } = "you";

        // Evento para refrescar UI
        public event Action? Changed;
        private void NotifyChanged() => Changed?.Invoke();

        // ---------------------------------------------------------
        // 1. OBTENER CONTEOS (Sidebar)
        // ---------------------------------------------------------
        public async Task<(int todos, int mios, int sinAsignar, int equipo)> GetCountsAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Filtramos solo los que no están "cerrados" si tuvieras estatus cerrado. 
            // Asumo Status = 0 es abierto.
            var baseQuery = db.CrmThreads.AsNoTracking().Where(t => t.IsActive == true);

            var todos = await baseQuery.CountAsync(ct);
            var mios = await baseQuery.CountAsync(t => t.AssignedTo == CurrentUser, ct);

            // Sin asignar: nulo o vacío
            var sinAsignar = await baseQuery.CountAsync(t => t.AssignedTo == null || t.AssignedTo == "", ct);

            // Equipo: asignado a alguien que no soy yo
            var equipo = await baseQuery.CountAsync(t => t.AssignedTo != null && t.AssignedTo != "" && t.AssignedTo != CurrentUser, ct);

            return (todos, mios, sinAsignar, equipo);
        }


        // ---------------------------------------------------------
        // 2. OBTENER LISTA DE HILOS (ThreadList)
        // ---------------------------------------------------------
        public async Task<IReadOnlyList<CrmThread>> GetThreadsAsync(InboxFilter filter, string? search = null, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            IQueryable<CrmThread> q = db.CrmThreads.AsNoTracking();

            // Aplicar Filtro de Pestañas
            q = filter switch
            {
                InboxFilter.Mios => q.Where(t => t.AssignedTo == CurrentUser),
                InboxFilter.SinAsignar => q.Where(t => t.AssignedTo == null || t.AssignedTo == ""),
                InboxFilter.Equipo => q.Where(t => t.AssignedTo != null && t.AssignedTo != "" && t.AssignedTo != CurrentUser),
                _ => q // Todos
            };

            // Aplicar Búsqueda (Search bar)
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                q = q.Where(t =>
                    (t.CustomerDisplayName != null && t.CustomerDisplayName.ToLower().Contains(s)) ||
                    (t.CustomerPhone != null && t.CustomerPhone.Contains(s)) ||
                    (t.CustomerEmail != null && t.CustomerEmail.ToLower().Contains(s)) ||
                    (t.LastMessagePreview != null && t.LastMessagePreview.ToLower().Contains(s))
                );
            }

            // Ordenar: Lo más reciente arriba (usando LastMessageUtc, fallback a CreatedUtc)
            q = q.OrderByDescending(t => t.LastMessageUtc ?? t.CreatedUtc);

            // Traemos los datos (puedes poner .Take(50) para paginar)
            return await q.ToListAsync(ct);
        }

        // ---------------------------------------------------------
        // 3. OBTENER DETALLE DE UN HILO (ChatView)
        // ---------------------------------------------------------
        public async Task<CrmThread?> GetThreadByIdAsync(string threadId, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var thread = await db.CrmThreads
                .AsNoTracking()
                //.Include(t => t.Messages) // Eager loading de mensajes
                //    .ThenInclude(m => m.Media) // Si quieres cargar info del media
                .FirstOrDefaultAsync(t => t.ThreadId == threadId, ct);

            if (thread == null) return null;

            // --- CORRECCIÓN CRÍTICA: ROMPER REFERENCIA CIRCULAR ---
            if (thread.Messages != null)
            {
                foreach (var msg in thread.Messages)
                {
                    // Cortamos el ciclo infinito. El hijo ya no apunta al padre.
                    msg.Thread = null!;
                }

                // Ordenamos en memoria
                thread.Messages = thread.Messages
                    .OrderBy(m => m.TimestampUtc)
                    .ToList();
            }

            return thread;
        }

        // Agrega esto dentro de tu clase CrmInboxRepository

        public async Task<List<ThreadSummaryDto>> GetThreadSummariesAsync(InboxFilter filter, string? search = null, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            IQueryable<CrmThread> q = db.CrmThreads.AsNoTracking().Where(t => t.IsActive == true);

            // 1. Aplicar Filtros (Misma lógica que tenías)
            q = filter switch
            {
                InboxFilter.Mios => q.Where(t => t.AssignedTo == CurrentUser),
                InboxFilter.SinAsignar => q.Where(t => string.IsNullOrEmpty(t.AssignedTo)),
                InboxFilter.Equipo => q.Where(t => !string.IsNullOrEmpty(t.AssignedTo) && t.AssignedTo != CurrentUser),
                _ => q
            };

            // 2. Aplicar Búsqueda (Optimizada)
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                q = q.Where(t =>
                    (t.CustomerDisplayName != null && t.CustomerDisplayName.ToLower().Contains(s)) ||
                    (t.CustomerPhone != null && t.CustomerPhone.Contains(s))
                );
                // Nota: Quité buscar en el body del mensaje para agilizar, agrégalo solo si es vital.
            }

            // 3. Ordenar y LA OPTIMIZACIÓN REAL
            return await q
                .OrderByDescending(t => t.LastMessageUtc ?? t.CreatedUtc)
                .Take(100) // <--- ¡ESTO SALVARÁ TU SERVIDOR! Solo trae los 100 más recientes.
                .Select(t => new ThreadSummaryDto
                {
                    // Solo traemos las columnas necesarias. SQL Server agradece esto.
                    ThreadId = t.ThreadId,
                    // Resolvemos el nombre aquí para no procesarlo en memoria
                    DisplayName = t.CustomerDisplayName ?? t.CustomerPhone ?? "Desconocido",
                    LastMessagePreview = t.LastMessagePreview,
                    LastMessageUtc = t.LastMessageUtc ?? t.CreatedUtc,
                    UnreadCount = t.UnreadCount,
                    Channel = t.Channel,
                    AssignedTo = t.AssignedTo,
                    PhotoUrl = t.CustomerPhotoUrl,
                    IsActive = t.IsActive
                })
                .ToListAsync(ct);
        }

        // En CrmInboxRepository.cs

        public async Task<List<CrmMessage>> GetMessagesPagedAsync(string publicThreadId, int skip, int take, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // 1. OPTIMIZACIÓN: Primero obtenemos el ID Numérico Interno (PK)
            // Esto es mucho más rápido que hacer un JOIN con la tabla de Threads en cada consulta.
            var internalId = await db.CrmThreads
                .AsNoTracking()
                .Where(t => t.ThreadId == publicThreadId)
                .Select(t => t.Id) // Solo traemos el int, ultra ligero
                .FirstOrDefaultAsync(ct);

            // Si no encontramos el hilo, regresamos lista vacía para no romper nada
            if (internalId == 0) return new List<CrmMessage>();

            // 2. Ahora buscamos los mensajes usando el ID numérico (int con int)
            var mensajes = await db.CrmMessages
                .AsNoTracking()
                .Where(m => m.ThreadRefId == internalId) // <-- AQUÍ estaba el error, ahora comparamos int vs int
                .OrderByDescending(m => m.TimestampUtc)  // Traemos los más nuevos primero
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            // 3. Los invertimos para visualizarlos correctamente (Cronológico: Antiguo -> Nuevo)
            mensajes.Reverse();

            return mensajes;
        }


        // ---------------------------------------------------------
        // 4. ASIGNAR AGENTE
        // ---------------------------------------------------------
        public async Task<bool> AssignAsync(string threadId, string? agentUser, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var rows = await db.CrmThreads
                .Where(t => t.ThreadId == threadId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssignedTo, agentUser), ct);

            if (rows > 0)
            {
                NotifyChanged();
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------
        // 5. MARCAR COMO LEÍDO
        // ---------------------------------------------------------
        public async Task MarkReadAsync(string threadId, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Resetear UnreadCount a 0
            var rows = await db.CrmThreads
                .Where(t => t.ThreadId == threadId && t.UnreadCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UnreadCount, 0), ct);

            if (rows > 0)
            {
                NotifyChanged();
            }
        }

        // ---------------------------------------------------------
        // 6. ENVIAR MENSAJE (AGENTE -> CLIENTE)
        // ---------------------------------------------------------
        public async Task<CrmMessage?> AppendAgentMessageAsync(string threadId, string text, string senderName, string? quotedId = null, CancellationToken ct = default)
        {   
            _logger.LogInformation("[CRMINBOX].[AppendAgentMessageAsync] MENSAJE | Iniciando envío de mensaje. Thread: {ThreadId}, Hacia: {Sender}", threadId, senderName);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var thread = await db.CrmThreads.FirstOrDefaultAsync(t => t.ThreadId == threadId, ct);
            if (thread == null)
            {   
                _logger.LogError("[CRMINBOX].[AppendAgentMessageAsync] ERROR | No se encontró el ThreadId {ThreadId} en la base de datos", threadId);
                return null;
            }

            try
            {
                // 1. OBTENER CONFIGURACIÓN DE EVOLUTION
                var apiUrl = _cfg["Evolution:ApiUrl"]?.TrimEnd('/');
                var apiKey = _cfg["Evolution:ApiKey"];
                var instance = _cfg["Evolution:InstanceName"];

                // Log de Configuración (No imprimas el ApiKey completo por seguridad)
                //Console.WriteLine($"[CRM] Config: Url={apiUrl}, Instance={instance}, KeyPresent={!string.IsNullOrEmpty(apiKey)}");

                if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogError("[CRMINBOX].[AppendAgentMessageAsync] ERROR | Configuración de Evolution incompleta (Url/Key).");                                      
                    return null;
                }


                // 1. CONSTRUIR EL PAYLOAD DE RESPUESTA (QUOTED)
                object? quotedPayload = null;
                string? quotedTextFound = null;

                if (!string.IsNullOrWhiteSpace(quotedId))
                {
                    // Buscamos el mensaje original en nuestra DB
                    var originalMsg = await db.CrmMessages.FirstOrDefaultAsync(m => m.ExternalId == quotedId, ct);
                    if (originalMsg != null)
                    {
                        quotedTextFound = originalMsg.Text;

                        // Construimos el objeto complejo que Evolution requiere para no fallar
                        quotedPayload = new
                        {
                            key = new
                            {
                                id = originalMsg.ExternalId,
                                fromMe = !originalMsg.DirectionIn, // Si DirectionIn es false, fue enviado por el agente
                                remoteJid = thread.ThreadId.Replace("wa:", "") + (thread.ThreadId.Contains("@") ? "" : "@s.whatsapp.net")
                            },
                            message = new
                            {
                                conversation = originalMsg.Text // El texto que aparecerá en el recuadro gris de WhatsApp
                            }
                        };
                        _logger.LogInformation("[CRMINBOX].[AppendAgentMessageAsync] PROCESO | Mensaje citado detectado: {QuotedId}", quotedId);
                    }
                }

                // 2. ENVIAR A EVOLUTION API (WHATSAPP REAL)
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

                var payload = new
                {
                    number = thread.CustomerPhone,
                    text = text,
                    quoted = quotedPayload, // Enviamos el objeto complejo o null
                    delay = 1200,
                    linkPreview = true
                };
                
                _logger.LogInformation("[CRMINBOX].[AppendAgentMessageAsync].[API_EVO] POST | Enviando a: {ApiUrl}/message/sendText/{Instance} | Destino: {Phone}", apiUrl, instance, thread.CustomerPhone);

                var response = await _httpClient.PostAsJsonAsync($"{apiUrl}/message/sendText/{instance}", payload, ct);

                // LOG DE RESPUESTA HTTP
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError("[CRMINBOX].[AppendAgentMessageAsync].[API_EVO] ERROR | Status: {Status}. Detalle: {Error}", response.StatusCode, errorContent);                    
                    return null;
                }

                _logger.LogInformation("[CRMINBOX].[AppendAgentMessageAsync].[API_EVO] ÉXITO | Evolution aceptó el mensaje.");                

                // LEER JSON RAW (A veces el modelo falla si Evolution cambia algo)
                var rawJsonResponse = await response.Content.ReadAsStringAsync(ct);
                //Console.WriteLine($"[CRM] Respuesta Raw de Evolution: {rawJsonResponse}");

                var evolutionResult = await response.Content.ReadFromJsonAsync<EvolutionSendResponse>(cancellationToken: ct);

                var rawHash = EvolutionParser.ComputeSha256(rawJsonResponse);

                // 3. LOGICA DE FECHAS (MEXICO)
                TimeZoneInfo mexicoZone;
                try { mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
                catch { mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
                var nowMexico = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);

                // 1. Extraer el ID correctamente (Según tu imagen de Debugger es raíz -> key -> id)
                var externalId = evolutionResult?.key?.id;

                // o usar el actual convertido a Unix para consistencia
                long unixTimestamp = evolutionResult?.messageTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // 3. Definir el tipo de mensaje (como es sendText, es conversation)
                string mediaType = "conversation";

                // 4. GUARDAR EN DB LOCAL
                var msg = new CrmMessage
                {
                    ThreadRefId = thread.Id,
                    Sender = senderName ?? CurrentUser,
                    DisplayName = senderName ?? CurrentUser,
                    Text = text,
                    DirectionIn = false,
                    TimestampUtc = nowMexico,
                    CreatedUtc = nowMexico,
                    MessageKind = 0,
                    ExternalId = externalId,
                    WaMessageId = externalId,
                    RawPayload = rawJsonResponse,
                    MediaType = mediaType,
                    QuotedMessageId = quotedPayload != null ? quotedId : null,
                    QuotedMessageText = quotedTextFound,
                    UpdatedAt = nowMexico,
                    RawHash = rawHash // AQUÍ ASIGNAMOS EL HASH CALCULADO
                };

                db.CrmMessages.Add(msg);
                thread.LastMessageUtc = nowMexico;
                thread.LastMessagePreview = text;

                await db.SaveChangesAsync(ct);

                _logger.LogInformation("[CRMINBOX].[AppendAgentMessageAsync].[DATABASE] GUARDADO | Mensaje insertado con ID Local: {Id} | ExtId: {ExtId}", msg.Id, externalId);                

                NotifyChanged();
                return msg;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CRMINBOX].[AppendAgentMessageAsync] EXCEPCIÓN CRÍTICA | Thread: {ThreadId} | Error: {Message}", threadId, ex.Message);
                
                if (ex.InnerException != null) Console.WriteLine($"[CRM] Inner Exception: {ex.InnerException.Message}");
                return null;
            }
        }


        // ---------------------------------------------------------
        // 7. GUARDAR/EDITAR CONTACTO (Modal)
        // ---------------------------------------------------------
        public async Task<int> UpsertContactAsync(int channel, string businessAccountId, string displayName, string? phone, string? email, string? company, CancellationToken ct = default)
        {
            // NOTA: Aquí adapto la lógica para actualizar la tabla CrmThreads directamente
            // o tu tabla de Contactos si la tienes separada. 
            // Dado que en el código anterior usabas un SP, aquí usaré EF Core sobre CrmThread
            // para simplificar, asumiendo que quieres actualizar los datos del cliente EN EL HILO.

            // Si tienes una tabla CrmContacts separada, ajusta este código para hacer Update/Add ahí.

            // Lógica: Actualizar todos los hilos que coincidan con ese teléfono/email
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Buscamos hilos que coincidan con el identificador principal (phone)
            // Si quieres buscar por PlatformId también, necesitarías pasarlo.
            if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email)) return 0;

            var query = db.CrmThreads.AsQueryable();

            if (!string.IsNullOrWhiteSpace(phone))
                query = query.Where(t => t.CustomerPhone == phone);
            else if (!string.IsNullOrWhiteSpace(email))
                query = query.Where(t => t.CustomerEmail == email);

            // Ejecutamos Update masivo
            var rows = await query.ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CustomerDisplayName, displayName)
                .SetProperty(t => t.CustomerEmail, email)
                .SetProperty(t => t.CustomerPhone, phone)
                .SetProperty(t => t.CompanyId, company), ct);

            if (rows > 0) NotifyChanged();

            return rows;
        }

      


        public async Task<int> SyncMessagesFromEvolutionAsync(string threadId, string customerPhone, int limit = 100)
        {
            // 1. Configuración
            var apiUrl = _cfg["Evolution:ApiUrl"]?.TrimEnd('/');
            var apiKey = _cfg["Evolution:ApiKey"];
            var instance = _cfg["Evolution:InstanceName"];

            // Normalizamos el remoteJid para la consulta inicial
            string remoteJid = customerPhone.Contains("@") ? customerPhone : $"{customerPhone.Trim()}@s.whatsapp.net";
            string numberClean = new string(remoteJid.Split('@')[0].Where(char.IsDigit).ToArray());

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

            // PASO 1: Despertar la instancia
            try
            {
                var checkUrl = $"{apiUrl}/chat/whatsappNumbers/{instance}";
                await _httpClient.PostAsJsonAsync(checkUrl, new { numbers = new[] { numberClean } });
            }
            catch { /* Silencioso */ }

            // PASO 2: Consultar Evolution
            var findUrl = $"{apiUrl}/chat/findMessages/{instance}";
            var payload = new
            {
                where = new { remoteJid = remoteJid },
                options = new { limit = limit, order = "DESC" }
            };

            var response = await _httpClient.PostAsJsonAsync(findUrl, payload);
            if (!response.IsSuccessStatusCode) return 0;

            var jsonRaw = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            List<EvolutionMessage> evoMessages = null;
            using (JsonDocument doc = JsonDocument.Parse(jsonRaw))
            {
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("messages", out JsonElement messagesElement) &&
                    messagesElement.TryGetProperty("records", out JsonElement recordsElement))
                {
                    evoMessages = JsonSerializer.Deserialize<List<EvolutionMessage>>(recordsElement.GetRawText(), options);
                }
            }

            if (evoMessages == null || !evoMessages.Any()) return 0;

            await using var db = await _dbFactory.CreateDbContextAsync();
            int guardados = 0;

            // Pre-cargamos el thread principal por si acaso
            var defaultThread = await db.CrmThreads.FirstOrDefaultAsync(t => t.ThreadId == threadId);

            foreach (var evoMsg in evoMessages)
            {
                string msgRemoteJid = evoMsg.Key.RemoteJid; // Puede venir "65507076079663@lid" o "@s.whatsapp.net"

                // --- BÚSQUEDA DEL PAPÁ (THREAD) EN 3 NIVELES ---

                // Nivel 1: Buscar por coincidencia exacta en ThreadId o CustomerLid
                var actualThread = await db.CrmThreads.FirstOrDefaultAsync(t =>
                    t.ThreadId == msgRemoteJid ||
                    t.CustomerLid == msgRemoteJid);

                // Nivel 2: Si no se halló (por ejemplo, si el JID en la DB no tiene el @lid o tiene formato distinto)
                if (actualThread == null)
                {
                    // Extraemos solo los números: "65507076079663"
                    string cleanMsgNum = new string(msgRemoteJid.Where(char.IsDigit).ToArray());

                    if (!string.IsNullOrEmpty(cleanMsgNum))
                    {
                        // Buscamos si el número limpio está contenido en alguna de las dos columnas
                        actualThread = await db.CrmThreads.FirstOrDefaultAsync(t =>
                            t.ThreadId.Contains(cleanMsgNum) ||
                            (t.CustomerLid != null && t.CustomerLid.Contains(cleanMsgNum)));
                    }
                }

                // Nivel 3: Red de seguridad (el thread pasado por parámetro)
                actualThread ??= defaultThread;

                if (actualThread == null)
                {
                    Console.WriteLine($"Ignorado: No se encontró thread para JID/LID {msgRemoteJid}");
                    continue;
                }

                // --- RESTO DE LA LÓGICA DE INSERCIÓN ---

                if (await db.CrmMessages.AnyAsync(m => m.ExternalId == evoMsg.Key.Id)) continue;

                bool esEntrante = !evoMsg.Key.FromMe;
                string emisor = evoMsg.PushName; // esEntrante ? (evoMsg.PushName ?? "Cliente") : "Agente";

                string contenido = evoMsg.Message?.Conversation
                                   ?? evoMsg.Message?.ExtendedTextMessage?.Text
                                   ?? (evoMsg.MessageType == "imageMessage" ? "[Imagen]" : $"[{evoMsg.MessageType}]");

                // Definir la zona horaria de México (Central Standard Time)
                var mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

                // A. Convertimos el Unix Timestamp de Evolution a UTC primero
                var utcDateTime = DateTimeOffset.FromUnixTimeSeconds(evoMsg.MessageTimestamp).UtcDateTime;

                // B. Convertimos esa hora UTC a la hora local de México
                var mexicoDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, mexicoZone);

                var msg = new CrmMessage
                {
                    ThreadRefId = actualThread.Id, // Usamos el ID de la tabla (int/long)
                    ExternalId = evoMsg.Key.Id,
                    Sender = emisor,
                    Text = contenido,
                    DirectionIn = esEntrante,
                    TimestampUtc = mexicoDateTime,
                    CreatedUtc = mexicoDateTime,
                    RawHash = evoMsg.Key.Id,
                    RawPayload = JsonSerializer.Serialize(evoMsg, options),
                    MessageKind = (evoMsg.MessageType == "stickerMessage") ? 5 : 0,
                    ExternalTimestamp = evoMsg.MessageTimestamp
                };

                db.CrmMessages.Add(msg);
                guardados++;
            }

            try
            {
                if (guardados > 0)
                {
                    await db.SaveChangesAsync();
                    Console.WriteLine($"Sincronización exitosa: {guardados} mensajes recuperados de Postgres.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error SQL: {ex.Message}");
            }

            return guardados;
        }


        public async Task<bool> GarantizarConfiguracionFijaAsync()
        {
            try
            {
                var apiUrl = _cfg["Evolution:ApiUrl"]?.TrimEnd('/');
                var apiKey = _cfg["Evolution:ApiKey"];
                var instance = _cfg["Evolution:InstanceName"];

                var endpoint = $"{apiUrl}/settings/set/{instance}";

                _logger.LogInformation("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[GarantizarConfiguracionFijaAsync] POST | Inicio de configuración fija para instancia: {Instance}", instance);

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

                var settings = new
                {
                    rejectCall = false,
                    //msgCall = "No se aceptan llamadas por este medio, por favor escriba.",
                    groupsIgnore = true,
                    alwaysOnline = false,
                    readMessages = false, // Visto manual habilitado
                    readStatus = false,
                    syncFullHistory = true
                };

                var response = await _httpClient.PostAsJsonAsync(endpoint, settings);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[GarantizarConfiguracionFijaAsync] ÉXITO | Configuración de '{Instance}' sincronizada correctamente.", instance);                    
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[GarantizarConfiguracionFijaAsync] ERROR | No se pudo sincronizar configuración. Status: {Status} | Detalle: {Error}", response.StatusCode, errorBody);

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WEBHOOKAPI].[CRMINBOXREPOSITORY].[GarantizarConfiguracionFijaAsync] EXCEPCIÓN CRÍTICA | Fallo al intentar conectar con Evolution API.");                
                return false;
            }
        }

        public async Task<bool> ConfigurarWebhooksAsync()
        {
            try
            {
                // 1. Obtener toda la configuración dinámicamente
                var apiUrl = _cfg["Evolution:ApiUrl"]?.TrimEnd('/');
                var apiKey = _cfg["Evolution:ApiKey"];
                var instance = _cfg["Evolution:InstanceName"];
                var webhookUrl = _cfg["Evolution:WebhookUrl"]; // <--- Leído desde settings

                // LOG DE INTENTO                
                _logger.LogInformation("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[ConfigurarWebhooksAsync] POST | Intentando registrar Webhook. URL: {WebhookUrl} | Instancia: {Instance}", webhookUrl, instance);

                if (string.IsNullOrEmpty(webhookUrl))
                {
                    _logger.LogWarning("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[ConfigurarWebhooksAsync] ADVERTENCIA | 'WebhookUrl' no configurada en los settings. Abortando registro.");                    
                    return false;                    
                }

                // 2. Endpoint correcto para Webhooks
                var endpoint = $"{apiUrl}/webhook/set/{instance}";

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

                // 3. Payload con la URL dinámica y todos los eventos                
                var payload = new
                {
                    webhook = new
                    {
                        enabled = true,
                        url = webhookUrl,
                        byEvents = false,
                        base64 = false,
                        events = new[] {
                            "APPLICATION_STARTUP", "QRCODE_UPDATED", "MESSAGES_SET", "MESSAGES_UPSERT",
                            "MESSAGES_UPDATE", "MESSAGES_DELETE", "SEND_MESSAGE", "CONTACTS_SET",
                            "CONTACTS_UPSERT", "CONTACTS_UPDATE", "PRESENCE_UPDATE", "CHATS_SET",
                            "CHATS_UPSERT", "CHATS_UPDATE", "CHATS_DELETE", "GROUPS_UPSERT",
                            "GROUP_UPDATE", "GROUP_PARTICIPANTS_UPDATE", "CONNECTION_UPDATE",
                            "LABELS_EDIT", "LABELS_ASSOCIATION", "CALL", "TYPEBOT_START",
                            "TYPEBOT_CHANGE_STATUS", "LOGOUT_INSTANCE", "REMOVE_INSTANCE"
                        }
                    }
                };

                // 4. Ejecutar POST
                var response = await _httpClient.PostAsJsonAsync(endpoint, payload);

                if (response.IsSuccessStatusCode)
                {                    
                    _logger.LogInformation("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[ConfigurarWebhooksAsync] ÉXITO | Webhook vinculado correctamente a: {WebhookUrl}", webhookUrl);                    
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync();                
                _logger.LogError("[WEBHOOKAPI].[CRMINBOXREPOSITORY].[ConfigurarWebhooksAsync] ERROR | Fallo en Evolution API. Detalle: {Error}", error);                
                return false;
            }
            catch (Exception ex)
            {   
                _logger.LogCritical(ex, "[WEBHOOKAPI].[CRMINBOXREPOSITORY].[ConfigurarWebhooksAsync] EXCEPCIÓN CRÍTICA | Fallo catastrófico al configurar Webhooks.");                
                return false;
            }
        }


        public async Task UpdateContactProfileAsync(string remoteJid, string? name, string? photo)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();

                // Buscamos el hilo por su ID de WhatsApp (JID)
                var thread = await db.CrmThreads.FirstOrDefaultAsync(t => t.ThreadId == remoteJid);

                if (thread != null)
                {
                    bool huboCambios = false;

                    // Solo actualizamos si el nombre no es nulo y es diferente
                    if (!string.IsNullOrEmpty(name) && thread.CustomerDisplayName != name)
                    {
                        thread.CustomerDisplayName = name;
                        huboCambios = true;
                    }

                    // Actualizamos la foto si recibimos una nueva URL
                    if (!string.IsNullOrEmpty(photo) && thread.CustomerPhotoUrl != photo)
                    {
                        thread.CustomerPhotoUrl = photo;
                        huboCambios = true;
                    }

                    if (huboCambios)
                    {
                        await db.SaveChangesAsync();
                        //_log.LogInformation($"Perfil actualizado para {remoteJid}: {name}");
                    }
                }
            }
            catch (Exception ex)
            {
                //_log.LogError(ex, $"Error actualizando perfil de contacto para {remoteJid}");
            }
        }

        public async Task<bool> SendReactionAsync(string threadId, string messageId, string emoji, CancellationToken ct = default)
        {
            try
            {
                var apiUrl = _cfg["Evolution:ApiUrl"]?.TrimEnd('/');
                var apiKey = _cfg["Evolution:ApiKey"];
                var instance = _cfg["Evolution:InstanceName"];

                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var originalMsg = await db.CrmMessages.FirstOrDefaultAsync(m => m.ExternalId == messageId, ct);
                if (originalMsg == null) return false;

                // --- LIMPIEZA EXTREMA DEL EMOJI ---
                string cleanEmoji = emoji ?? "";

                // Si es el corazón rojo conflictivo, lo forzamos a su versión simple (sin el selector \uFE0F)
                if (cleanEmoji.Contains("❤️"))
                {
                    cleanEmoji = "\u2764";
                }
                else if (!string.IsNullOrEmpty(cleanEmoji))
                {
                    // Tomamos solo el primer "Cluster" de texto para asegurar que sea un solo emoji
                    var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(cleanEmoji);
                    if (enumerator.MoveNext())
                    {
                        cleanEmoji = enumerator.GetTextElement();
                    }
                }

                string cleanNumber = new string(threadId.Where(char.IsDigit).ToArray());
                string remoteJid = $"{cleanNumber}@s.whatsapp.net";

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

                var payload = new
                {
                    number = cleanNumber,
                    reaction = cleanEmoji,
                    key = new
                    {
                        remoteJid = remoteJid,
                        fromMe = !originalMsg.DirectionIn,
                        id = messageId
                    }
                };

                // Log para ver qué estamos mandando exactamente
                Console.WriteLine($"[CRM] Enviando Reacción: '{cleanEmoji}' al mensaje {messageId}");

                var response = await _httpClient.PostAsJsonAsync($"{apiUrl}/message/sendReaction/{instance}", payload, ct);

                if (response.IsSuccessStatusCode)
                {
                    originalMsg.Reaction = cleanEmoji;
                    await db.SaveChangesAsync(ct);
                    NotifyChanged();
                    return true;
                }

                var errorDetail = await response.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[CRM] Error 400 persistente: {errorDetail}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRM] Excepción: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> UpdateMessageAsync(string threadId, string messageId, string newText, CancellationToken ct = default)
        {
            // 1. Obtención de configuraciones
            var apiUrl = _cfg["Evolution:ApiUrl"]?.TrimEnd('/');
            var apiKey = _cfg["Evolution:ApiKey"];
            var instance = _cfg["Evolution:InstanceName"];

            // 2. Validación del mensaje en DB local (opcional, pero recomendado para obtener el ID correcto)
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var originalMsg = await db.CrmMessages.FirstOrDefaultAsync(m => m.ExternalId == messageId, ct);
            if (originalMsg == null) return false;

            // 3. Limpieza de emojis en el nuevo texto (reutilizando tu lógica de éxito)
            string processedText = newText ?? "";
            if (processedText.Contains("❤️"))
            {
                processedText = processedText.Replace("❤️", "\u2764");
            }

            // 4. Preparación del identificador del destinatario
            string cleanNumber = new string(threadId.Where(char.IsDigit).ToArray());
            string remoteJid = $"{cleanNumber}@s.whatsapp.net";

            // 5. Configuración de Headers
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

            // 6. Construcción del Body según el endpoint /chat/updateMessage
            var body = new
            {
                number = remoteJid,
                key = new
                {
                    remoteJid = remoteJid,
                    fromMe = true,
                    id = messageId // El ExternalId de WhatsApp
                },
                text = processedText
            };

            try
            {
                // 7. Envío de la petición
                var response = await _httpClient.PostAsJsonAsync($"{apiUrl}/chat/updateMessage/{instance}", body, ct);

                if (response.IsSuccessStatusCode)
                {
                    // Actualizamos la base de datos local para que el cambio sea persistente
                    originalMsg.Text = processedText;
                    originalMsg.UpdatedAt = DateTime.UtcNow; // Si tienes este campo
                    await db.SaveChangesAsync(ct);
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<bool> UpdateMessageStatusAsync(string remoteJid, string externalId, int newStatus)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Buscamos el mensaje por su ID de WhatsApp
            var msg = await db.CrmMessages
                 .FirstOrDefaultAsync(m => m.ExternalId == externalId);

            if (msg != null && msg.Status < newStatus) // Solo actualizamos si el nuevo status es "mayor"
            {
                msg.Status = newStatus;
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateThreadActiveStatusAsync(string threadId, string user, bool isActive)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var thread = await db.CrmThreads
                .FirstOrDefaultAsync(t => t.ThreadId == threadId);

            if (thread == null) return false;

            // 3. LOGICA DE FECHAS (MEXICO)
            TimeZoneInfo mexicoZone;
            try { mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
            catch { mexicoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
            var nowMexico = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);

            thread.IsActive = isActive;
            thread.UpdatedAt = nowMexico;
            thread.DeletedAt = nowMexico;

            // Creamos el registro de auditoría
            var log = new CrmLog
            {
                LogType = "CHAT_DELETE",
                Action = "SOFT_DELETE",
                ThreadId = threadId,
                User = user,
                Timestamp = nowMexico,
                Description = $"El usuario {user} movió la conversación de {thread.CustomerDisplayName} a la papelera."
            };

            db.CrmLogs.Add(log);

            // Guardamos cambios y retornamos true si se afectó al menos una fila
            return await db.SaveChangesAsync() > 0;
        }

        // Dentro de CrmInboxRepository.cs

        public async Task<List<ThreadSummaryDto>> GetArchivedThreadsAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            return await db.CrmThreads
                .AsNoTracking()
                .Where(t => t.IsActive == false) // <--- Solo los inactivos
                .OrderByDescending(t => t.UpdatedAt) // Los últimos eliminados primero
                .Select(t => new ThreadSummaryDto
                {
                    ThreadId = t.ThreadId,
                    DisplayName = t.CustomerDisplayName ?? t.CustomerPhone ?? "Desconocido",
                    LastMessagePreview = t.LastMessagePreview,
                    LastMessageUtc = t.LastMessageUtc ?? t.CreatedUtc,
                    UnreadCount = t.UnreadCount,
                    IsActive = t.IsActive
                })
                .ToListAsync(ct);
        }

        // Método para borrado físico (Permanente)
        public async Task<bool> HardDeleteThreadAsync(string threadId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // 1. Eliminar mensajes asociados primero (integridad referencial)
            var thread = await db.CrmThreads.Include(t => t.Messages)
                                 .FirstOrDefaultAsync(t => t.ThreadId == threadId);

            if (thread == null) return false;

            db.CrmThreads.Remove(thread);
            return await db.SaveChangesAsync() > 0;
        }


    }
}
