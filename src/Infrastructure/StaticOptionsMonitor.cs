using System;
using Microsoft.Extensions.Options;

namespace statsCollector.Infrastructure;

public sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    where T : class
{
    public T CurrentValue => currentValue ?? throw new ArgumentNullException(nameof(currentValue));

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        return new EmptyDisposable();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
