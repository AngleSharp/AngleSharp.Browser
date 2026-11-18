namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Io;

/// <summary>
/// Answers the requests of the custom schemes registered in
/// <see cref="BrowserOptions.SchemeHandlers"/>.
/// </summary>
internal sealed class SchemeRequester(IDictionary<string, Func<Request, CancellationToken, Task<IResponse?>>> handlers) : BaseRequester
{
    // a snapshot: the options may change once the engine has been created
    private readonly Dictionary<string, Func<Request, CancellationToken, Task<IResponse?>>> _handlers = new(handlers, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Schemes => _handlers.Keys;

    public override bool SupportsProtocol(string protocol) => _handlers.ContainsKey(protocol);

    protected override Task<IResponse?> PerformRequestAsync(Request request, CancellationToken cancel) =>
        _handlers.TryGetValue(request.Address.Scheme, out var handler) ? handler(request, cancel) : Task.FromResult<IResponse?>(null);
}
