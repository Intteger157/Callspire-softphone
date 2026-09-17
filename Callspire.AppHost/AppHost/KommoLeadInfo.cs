using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// UI-agnostic Kommo lead descriptor returned by <see cref="AmoCrmService"/> lead lookups.
    /// Replaces the former <c>LeadSelectionWindow.LeadInfo</c> nested type so the service
    /// no longer depends on the WPF window.
    /// </summary>
    public class KommoLeadInfo
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>Kommo responsible_user_id; null if not requested.</summary>
        public long? ResponsibleUserId { get; set; }
    }

    /// <summary>
    /// Everything a UI needs to show the manual "pick a lead" dialog for a finished call
    /// (local Kommo mode with <c>EnableLeadSelection</c>).
    /// </summary>
    public sealed class KommoLeadSelectionRequest
    {
        public required IReadOnlyList<KommoLeadInfo> Leads { get; init; }
        public string? Subdomain { get; init; }
        public string? PhoneNumber { get; init; }
        public string? AudioFilePath { get; init; }
        public bool IsIncoming { get; init; }
        public int DurationSeconds { get; init; }
        public bool WasAnswered { get; init; }
        public string? CallLog { get; init; }
        public DateTime? CallTime { get; init; }
    }

    /// <summary>Outcome of the manual lead picker. <c>null</c> from the handler means the user cancelled.</summary>
    public sealed class KommoLeadSelectionResult
    {
        public long LeadId { get; init; }
        /// <summary>True when the dialog itself already uploaded the recording/note to the chosen lead.</summary>
        public bool RecordingUploadedInDialog { get; init; }
    }

    /// <summary>
    /// Pluggable UI hook used by <see cref="AmoCrmService"/> to ask the user which lead a call belongs to.
    /// WPF and Avalonia shells register their own implementation; the service itself has no UI dependency.
    /// </summary>
    public static class KommoLeadSelectionUi
    {
        /// <summary>
        /// Handler that shows a lead picker and resolves with the selection, or <c>null</c> when cancelled.
        /// Must marshal to the UI thread itself.
        /// </summary>
        public static Func<KommoLeadSelectionRequest, Task<KommoLeadSelectionResult?>>? Handler { get; set; }
    }
}
