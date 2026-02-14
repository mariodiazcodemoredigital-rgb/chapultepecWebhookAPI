using Crm.Webhook.Core.Data;
using Crm.Webhook.Core.Data.Repositories.EvolutionWebHook;
using Crm.Webhook.Core.Parsers;
using Crm.Webhook.Core.Services.Hubs;
using Crm.Webhook.Core.Services.Implementation.EvolutionWebHook;
using Crm.Webhook.Core.Services.Interfaces.EvolutionWebHook;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// 1. Captura la cadena de conexión
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

Serilog.Debugging.SelfLog.Enable(msg => Console.WriteLine($"[SERILOG ERROR] {msg}"));

// 2. Configuración de Serilog con el Rolling File en la carpeta "Logs"
// Esta es la carpeta que mapeamos en Easypanel hacia /root/logs_webhook
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
   .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Evita ruido de Microsoft
   .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("Logs/webhook-audit-.txt",
        rollingInterval: RollingInterval.Day,
        buffered: false, // Desactiva el buffer para que escriba al instante
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1), // Fuerza el guardado cada segundo
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

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
            try
            {
                Log.Information("[WEBHOOKAPI].[PROGRAM].[Startup] INFO | Esperando 5s para estabilizacion de red en VPS...");
                await Task.Delay(5000); // Dale un poco más de tiempo en el VPS

                Log.Information("[WEBHOOKAPI].[PROGRAM].[Startup] PROCESO | Iniciando GarantizarConfiguracionFijaAsync...");
                await repo.GarantizarConfiguracionFijaAsync();

                Log.Information("[WEBHOOKAPI].[PROGRAM].[Startup] PROCESO | Iniciando ConfigurarWebhooksAsync...");
                await repo.ConfigurarWebhooksAsync();

                Log.Information("[WEBHOOKAPI].[PROGRAM].[Startup] EXITO | Microservicio sincronizado con Evolution API.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WEBHOOKAPI].[PROGRAM].[Startup] EXCEPCIÓN CRÍTICA | Fallo el hilo de configuración automática.");
            }
        });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[WEBHOOKAPI].[PROGRAM].[Startup] ERROR | No se pudo inicializar el Scope de configuración.");        
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

// --- BLOQUE DE AUDITORÍA ---
var config = app.Configuration;
var webhookUrl = config["Evolution:WebhookUrl"];
// Limpiamos la cadena de conexión para el log (sin passwords)
var dbLogInfo = connectionString?.Split(';').FirstOrDefault(x => x.StartsWith("Server") || x.StartsWith("Data Source"));

var provider = (config as IConfigurationRoot)?.Providers
    .LastOrDefault(p => p.TryGet("Evolution:WebhookUrl", out _));

Log.Information("----------------------------------------------------------------------------");
Log.Information("[WEBHOOKAPI].[PROGRAM].[Auditoria] SISTEMA INICIADO | WEBHOOK API CHAPULTEPEC");
Log.Information("[WEBHOOKAPI].[PROGRAM].[Auditoria] URL Webhook: {Url}", webhookUrl ?? "No definida");
Log.Information("[WEBHOOKAPI].[PROGRAM].[Auditoria] Origen Config: {Source}", provider?.ToString() ?? "Desconocido");
Log.Information("[WEBHOOKAPI].[PROGRAM].[Auditoria] Database Host: {Db}", dbLogInfo ?? "No disponible");
Log.Information("----------------------------------------------------------------------------");

app.Run();
