using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;

namespace Hmy.Estao.Core.Refresh;

public sealed class UsageRefreshService
{
    private readonly ConfigStore _configStore;
    private readonly UsageProviderFactory _providerFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyList<UsageSnapshot> _lastSnapshots = [];

    public UsageRefreshService(ConfigStore configStore, UsageProviderFactory? providerFactory = null)
    {
        _configStore = configStore;
        _providerFactory = providerFactory ?? new UsageProviderFactory();
    }

    public event EventHandler<IReadOnlyList<UsageSnapshot>>? Refreshed;

    public IReadOnlyList<UsageSnapshot> LastSnapshots => _lastSnapshots;

    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return _lastSnapshots;
        }

        try
        {
            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var enabledProviders = config.Providers.Where(provider => provider.Enabled == true && ProviderCatalog.IsSupported(provider.Id)).ToList();
            var snapshots = new List<UsageSnapshot>();

            foreach (var providerConfig in enabledProviders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = _providerFactory.Create(providerConfig.Id);
                var accounts = provider.GetAccounts(providerConfig);
                var selectedAccount = SelectAccount(providerConfig, accounts);
                var request = new FetchRequest(providerConfig, selectedAccount, ProviderHelpers.ParseSource(providerConfig.Source), cancellationToken);

                try
                {
                    snapshots.Add(await provider.FetchAsync(request).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    snapshots.Add(UsageSnapshot.Failure(provider.Id, providerConfig.Source ?? "auto", ex.Message));
                }
            }

            _lastSnapshots = snapshots;
            Refreshed?.Invoke(this, snapshots);
            return snapshots;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public static TimeSpan AdaptiveDelay(DateTimeOffset now, DateTimeOffset? lastInteraction, bool powerConstrained)
    {
        if (powerConstrained)
        {
            return TimeSpan.FromMinutes(30);
        }

        if (lastInteraction is null)
        {
            return TimeSpan.FromMinutes(30);
        }

        var age = now - lastInteraction.Value;
        if (age <= TimeSpan.FromMinutes(5))
        {
            return TimeSpan.FromMinutes(2);
        }

        if (age <= TimeSpan.FromHours(1))
        {
            return TimeSpan.FromMinutes(5);
        }

        if (age <= TimeSpan.FromHours(4))
        {
            return TimeSpan.FromMinutes(15);
        }

        return TimeSpan.FromMinutes(30);
    }

    private static ProviderAccount? SelectAccount(ProviderConfig config, IReadOnlyList<ProviderAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return null;
        }

        var activeIndex = config.ActiveAccountIndex ?? config.TokenAccounts?.ActiveIndex ?? 0;
        if (activeIndex >= 0 && activeIndex < accounts.Count)
        {
            return accounts[activeIndex];
        }

        return accounts[0];
    }
}

public sealed class AdaptiveRefreshLoop : IDisposable
{
    private readonly UsageRefreshService _service;
    private readonly Func<bool> _isPowerConstrained;
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _lastInteraction;

    public AdaptiveRefreshLoop(UsageRefreshService service, Func<bool>? isPowerConstrained = null)
    {
        _service = service;
        _isPowerConstrained = isPowerConstrained ?? (() => false);
    }

    public void MarkInteraction() => _lastInteraction = DateTimeOffset.UtcNow;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await _service.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = UsageRefreshService.AdaptiveDelay(DateTimeOffset.UtcNow, _lastInteraction, _isPowerConstrained());
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            await _service.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
