using FitCore.SharedKernel;

namespace FitCore.Infrastructure.Core;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
