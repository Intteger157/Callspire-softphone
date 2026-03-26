using Callspire.UpdateServer.Models;
using Callspire.UpdateServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Callspire.UpdateServer.Pages;

[Authorize]
public class DashboardModel : PageModel
{
    private readonly UpdateFileStore _store;

    public DashboardModel(UpdateFileStore store)
    {
        _store = store;
    }

    public UpdateManifest? Current { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }

    [BindProperty]
    public string? DraftVersion { get; set; }

    [BindProperty]
    public string? DraftNotes { get; set; }

    [BindProperty]
    public bool DraftMandatory { get; set; }

    public async Task OnGetAsync()
    {
        if (Request.Query["published"].FirstOrDefault() == "1")
            Message = "Published successfully.";
        if (Request.Query["deleted"].FirstOrDefault() == "1")
            Message = "Release deleted.";
        if (Request.Query["draft"].FirstOrDefault() == "1")
            Message = "Draft saved.";
        var err = Request.Query["err"].FirstOrDefault();
        if (!string.IsNullOrEmpty(err))
            Error = err;

        await LoadCurrentAsync().ConfigureAwait(false);

        var draft = await _store.ReadDraftAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (draft != null)
        {
            DraftVersion = string.IsNullOrWhiteSpace(draft.Version) ? Current?.Version : draft.Version;
            DraftNotes = draft.Notes ?? Current?.Notes;
            DraftMandatory = draft.Mandatory;
            return;
        }

        DraftVersion = Current?.Version;
        DraftNotes = Current?.Notes;
        DraftMandatory = Current?.Mandatory ?? false;
    }

    private async Task LoadCurrentAsync()
    {
        Current = await _store.ReadManifestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
