using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crm.Webhook.Core.Models
{
    public class CrmThread
    {
        public int Id { get; set; }

        // Identidad del thread (ej: whatsapp:ChapultepecEvo:5217774435965)
        public string ThreadId { get; set; } = null!;

        // WhatsApp / Evolution instance
        public string? BusinessAccountId { get; set; }

        // Canal (1 = WhatsApp, futuro: IG, FB, etc.)
        public int Channel { get; set; } = 1;

        // Clave compuesta opcional para búsquedas
        public string? ThreadKey { get; set; }

        // =========================
        // Datos del cliente
        // =========================
        public string? CustomerDisplayName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? CustomerEmail { get; set; }

        // remoteJid / platform id
        public string? CustomerPlatformId { get; set; }

        // =========================
        // Contexto negocio
        // =========================
        public string? CompanyId { get; set; }

        // =========================
        // Estado del inbox
        // =========================
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastMessageUtc { get; set; }

        public string? LastMessagePreview { get; set; }

        public int UnreadCount { get; set; } = 0;

        // 0 = Open, 1 = Pending, 2 = Closed (definir enum después)
        public int Status { get; set; } = 0;

        public string? AssignedTo { get; set; }

        public string? MainParticipant { get; set; }
        public string? CustomerLid { get; set; } // Nuevo campo

        // Nueva propiedad para la foto de perfil
        public string? CustomerPhotoUrl { get; set; }

        // Propiedad para el borrado lógico
        public bool IsActive { get; set; } = true;

        // Propiedad para auditoría y ordenamiento
        public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? DeletedAt { get; set; }

        // =========================
        // Navegación
        // =========================
        public ICollection<CrmMessage> Messages { get; set; } = new List<CrmMessage>();
    }
}
