using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.Service.Contracts;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// <see cref="IDesktopShell"/> whose "windows" live in the Swift process. Fire-and-forget shell calls
    /// become IPC events; modal prompts become IPC requests whose correlation id the Swift side answers.
    /// When Swift is not connected, prompts resolve to the same conservative defaults the Avalonia shell
    /// used on dialog failure (cancelled picker / "attach to contact").
    /// </summary>
    public sealed class RemoteDesktopShell : IDesktopShell
    {
        private static readonly TimeSpan ModalTimeout = TimeSpan.FromMinutes(10);

        private readonly IpcServer _ipc;
        private readonly CallSessionHost _calls;

        public RemoteDesktopShell(IpcServer ipc, CallSessionHost calls)
        {
            _ipc = ipc;
            _calls = calls;
        }

        public bool HasActiveCallWindow => _calls.HasActiveCall;
        public bool IsWebRtcCallActive => _calls.IsWebRtcCallActive;

        public void ShowCallWindow(CallWindowRequest request) => _calls.Open(request);

        public void ShowSettings() => _ = _ipc.SendEventAsync("showSettings");

        public async Task<ConnectionSelectionResult> ShowConnectionSelectionAsync(ConnectionSelectionRequest request)
        {
            try
            {
                var r = await _ipc.RequestAsync<ConnectionSelectionResultDto>("showConnectionSelection", ConnectionSelectionDto.From(request), ModalTimeout).ConfigureAwait(false);
                return new ConnectionSelectionResult
                {
                    Slot = r?.Slot?.ToLowerInvariant() switch
                    {
                        "main" => ConnectionSlot.Main,
                        "secondary" => ConnectionSlot.Secondary,
                        _ => null,
                    },
                    CallerId = r?.CallerId,
                };
            }
            catch (Exception ex)
            {
                AppLog.Log($"[RemoteShell] connection picker failed: {ex.Message}");
                return new ConnectionSelectionResult();
            }
        }

        public async Task<LeadSelectionResult> ShowLeadSelectionAsync(string phoneNumber)
        {
            try
            {
                var r = await _ipc.RequestAsync<LeadSelectionResultDto>("showLeadSelection", new { phoneNumber }, ModalTimeout).ConfigureAwait(false);
                if (r == null || r.Cancelled) return LeadSelectionResult.CancelledResult;
                return new LeadSelectionResult { Proceed = r.Proceed, LeadId = r.LeadId is > 0 ? r.LeadId : null };
            }
            catch (Exception ex)
            {
                AppLog.Log($"[RemoteShell] lead selection failed: {ex.Message}");
                return LeadSelectionResult.Contact;
            }
        }

        /// <summary>Local-mode picker (<see cref="KommoLeadSelectionUi.Handler"/>): leads found by <see cref="AmoCrmService"/>.</summary>
        public async Task<KommoLeadSelectionResult?> ShowKommoLeadPickerAsync(KommoLeadSelectionRequest request)
        {
            try
            {
                var dto = new KommoLeadPickerDto
                {
                    Subdomain = request.Subdomain, PhoneNumber = request.PhoneNumber, IsIncoming = request.IsIncoming,
                    DurationSeconds = request.DurationSeconds, WasAnswered = request.WasAnswered, CallTime = request.CallTime,
                    Leads = request.Leads.Select(l => new KommoLeadDto { Id = l.Id, Name = l.Name, Description = l.Description, ResponsibleUserId = l.ResponsibleUserId }).ToList(),
                };
                var r = await _ipc.RequestAsync<KommoLeadPickerResultDto>("showKommoLeadPicker", dto, ModalTimeout).ConfigureAwait(false);
                return r?.LeadId is > 0 ? new KommoLeadSelectionResult { LeadId = r.LeadId.Value } : null;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[RemoteShell] Kommo lead picker failed: {ex.Message}");
                return null;
            }
        }

        public void ShowMessage(string title, string text)
            => _ = _ipc.SendEventAsync("showMessage", new MessageDto { Title = title, Text = text });

        public void ShowUpdateAvailable(UpdateInfo info, string currentVersion)
            => _ = _ipc.SendEventAsync("showUpdateAvailable", new UpdateAvailableDto
            {
                Version = info.Version, CurrentVersion = currentVersion,
                Url = info.PlatformUrl, Sha256 = info.PlatformSha256 ?? info.Sha256, Notes = info.Notes,
            });

        public void BringToForeground() => _ = _ipc.SendEventAsync("bringToForeground");
    }
}
