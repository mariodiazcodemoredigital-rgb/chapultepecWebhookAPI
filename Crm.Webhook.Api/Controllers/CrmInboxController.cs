using Crm.Webhook.Core.Data.Repositories.EvolutionWebHook;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Webhook.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CrmInboxController : ControllerBase
    {
        private readonly CrmInboxRepository _repository;
        private readonly ILogger<CrmInboxController> _logger;

        public CrmInboxController(CrmInboxRepository repository, ILogger<CrmInboxController> logger)
        {
            _repository = repository;
            _logger = logger;
        }


        [HttpGet("counts")]
        public async Task<IActionResult> GetCounts(CancellationToken ct)
        {
            // Aplicamos tu patrón de logs
            _logger.LogInformation("[WEBHOOKAPI].[CRMINBOXCONTROLLER].[GetCounts] INFO | Solicitando conteos de hilos.");

            try
            {
                var counts = await _repository.GetCountsAsync(ct);
                return Ok(counts); // Devuelve el objeto (todos, mios, sinAsignar, equipo)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WEBHOOKAPI].[CRMINBOXCONTROLLER].[GetCounts] ERROR | Fallo al obtener conteos.");
                return StatusCode(500, "Error interno al obtener los conteos.");
            }
        }
    }
}
