using Microsoft.JSInterop;

namespace ExamOps.Infra.Ui;

public sealed class ToastService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public Task SuccessAsync(string title, string? description = null) => EmitAsync("ok", title, description);
    public Task ErrorAsync(string title, string? description = null) => EmitAsync("err", title, description);
    public Task InfoAsync(string title, string? description = null) => EmitAsync("info", title, description);
    public Task WorkingAsync(string title, string? description = null) => EmitAsync("wait", title, description);

    private async Task EmitAsync(string kind, string title, string? description)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./Components/Shared/ToastHost.razor.js");
            await _module.InvokeVoidAsync("emit", kind, title, description ?? "");
        }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
