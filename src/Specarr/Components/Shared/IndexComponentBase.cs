using Microsoft.AspNetCore.Components;
using Specarr.Library;

namespace Specarr.Components.Shared;

/// <summary>A page that redraws while the background scan reads files.</summary>
public abstract class IndexComponentBase : ComponentBase, IDisposable
{
    [Inject] protected LibraryIndex Index { get; set; } = null!;

    protected IndexSnapshot Snapshot => Index.Snapshot;

    protected override void OnInitialized()
    {
        Index.Changed += OnIndexChanged;
    }

    private void OnIndexChanged()
    {
        _ = InvokeAsync(() =>
        {
            OnIndexUpdated();
            StateHasChanged();
        });
    }

    /// <summary>Called on the page's thread before it redraws after a change.</summary>
    protected virtual void OnIndexUpdated()
    {
    }

    public virtual void Dispose()
    {
        Index.Changed -= OnIndexChanged;
        GC.SuppressFinalize(this);
    }
}
