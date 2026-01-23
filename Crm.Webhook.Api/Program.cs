using Crm.Webhook.Core.Data;
using Crm.Webhook.Core.Data.Repositories.EvolutionWebHook;
using Crm.Webhook.Core.Parsers;
using Crm.Webhook.Core.Services.Hubs;
using Crm.Webhook.Core.Services.Implementation.EvolutionWebHook;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Captura la cadena de conexión
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// 2. Registra el contexto con el Driver de SQL Server
builder.Services.AddDbContextFactory<CrmInboxDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
    {
        // Reintentos automáticos si el VPS tiene micro-cortes
        sql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);
    }));



// --- Background Services ---
builder.Services.AddSingleton<InMemoryMessageQueue>();
builder.Services.AddSingleton<IMessageQueue>(sp => sp.GetRequiredService<InMemoryMessageQueue>());

// --- Parsers ---
builder.Services.AddScoped<EvolutionParser>();

// --- Repositorios y Servicios de Persistencia ---
// Registramos primero el repositorio y luego el servicio que lo consume
builder.Services.AddScoped<EvolutionPersistenceRepository>();
builder.Services.AddScoped<IEvolutionPersistenceService, EvolutionPersistenceService>();

// --- Evolution Webhook Logic---
builder.Services.AddScoped<WebhookControlRepository>();
builder.Services.AddScoped<IWebhookControlService, WebhookControlService>();

builder.Services.AddHostedService<MessageProcessingService>();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<CrmInboxRepository>();


// Add services to the container.

builder.Services.AddControllers();

builder.Services.AddSignalR(options => {
    options.EnableDetailedErrors = true;
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options => {
    options.AddPolicy("AllowCRM", policy => {
        policy.WithOrigins("https://localhost:7004/") // El puerto de tu Blazor
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Crucial para SignalR
    });
});

var app = builder.Build();

// --- CONFIGURACIÓN AUTOMÁTICA DE EVOLUTION , PARA PRUEBAS USAMOS NGROK, EN PRODUCCION CAMBIAR EL APPSETTINGS ---
using (var scope = app.Services.CreateScope())
{
    try
    {
        var repo = scope.ServiceProvider.GetRequiredService<CrmInboxRepository>();
        _ = Task.Run(async () => {
            await Task.Delay(5000); // Dale un poco más de tiempo en el VPS

            // rejectCall = true, msgCall = "No se aceptan llamadas por este medio, por favor escriba.", groupsIgnore = true,
            //alwaysOnline = false, readMessages = false, // Visto manual habilitado readStatus = false,syncFullHistory = true
            await repo.GarantizarConfiguracionFijaAsync();

            //events = new[] {
            //"APPLICATION_STARTUP", "QRCODE_UPDATED", "MESSAGES_SET", "MESSAGES_UPSERT",
            //                "MESSAGES_UPDATE", "MESSAGES_DELETE", "SEND_MESSAGE", "CONTACTS_SET",
            //                "CONTACTS_UPSERT", "CONTACTS_UPDATE", "PRESENCE_UPDATE", "CHATS_SET",
            //                "CHATS_UPSERT", "CHATS_UPDATE", "CHATS_DELETE", "GROUPS_UPSERT",
            //                "GROUP_UPDATE", "GROUP_PARTICIPANTS_UPDATE", "CONNECTION_UPDATE",
            //                "LABELS_EDIT", "LABELS_ASSOCIATION", "CALL", "TYPEBOT_START",
            //                "TYPEBOT_CHANGE_STATUS", "LOGOUT_INSTANCE", "REMOVE_INSTANCE"
            // Evolution:WebhookUrl => https://98352ea10278.ngrok-free.app/api/webhook/evolution

            await repo.ConfigurarWebhooksAsync();

            Console.WriteLine("[Startup] Microservicio sincronizado con Evolution API.");
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Error: {ex.Message}");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHub<CrmHub>("/crmHub"); // SignalR

app.Run();
