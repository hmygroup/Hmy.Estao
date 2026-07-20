using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Providers;

public interface IUsageProvider
{
    string Id { get; }

    IReadOnlyList<ProviderAccount> GetAccounts(ProviderConfig config);

    Task<UsageSnapshot> FetchAsync(FetchRequest request);
}

public sealed class ProviderException : Exception
{
    public ProviderException(string message)
        : base(message)
    {
    }
}
