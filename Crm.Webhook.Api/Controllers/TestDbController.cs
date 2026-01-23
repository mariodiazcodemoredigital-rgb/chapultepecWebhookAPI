using Crm.Webhook.Core.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crm.Webhook.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TestDbController : ControllerBase
    {
        private readonly IDbContextFactory<CrmInboxDbContext> _contextFactory;

        public TestDbController(IDbContextFactory<CrmInboxDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        [HttpGet("check")]
        public async Task<IActionResult> CheckConnection()
        {
            try
            {
                await using var db = await _contextFactory.CreateDbContextAsync();

                // Intenta realizar una operación ultra ligera: contar los hilos activos
                var totalThreads = await db.CrmThreads.CountAsync();

                return Ok(new
                {
                    status = "Success",
                    message = "Conexión establecida con SQL Server",
                    database = db.Database.GetDbConnection().Database,
                    threadsFound = totalThreads,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    status = "Error",
                    message = "No se pudo conectar a la base de datos",
                    details = ex.Message,
                    inner = ex.InnerException?.Message
                });
            }
        }
    }
}
