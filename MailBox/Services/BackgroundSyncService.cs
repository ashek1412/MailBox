using MailBox.Models;
using Microsoft.Extensions.Hosting;

namespace MailBox.Services;

public class BackgroundSyncService : BackgroundService
{
    private readonly AccountRepository              _accounts;
    private readonly ImapSyncService                _imap;
    private readonly Dictionary<int, SemaphoreSlim> _accountLocks = new();
    private volatile bool                           _paused;

    public event Action<AccountModel, int>?  NewMailArrived;
    public event Action<string>?             SyncingAccount;
    public event Action<string, string>?     AccountProgress; // (email, message)

    public BackgroundSyncService(AccountRepository accounts, ImapSyncService imap)
    {
        _accounts = accounts;
        _imap     = imap;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            if (!_paused)
                await SyncAllAsync(ct);

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    public Task SyncAllAsync(CancellationToken ct = default)
    {
        var accounts = _accounts.GetAll();
        return Task.WhenAll(accounts.Select(a => SyncAccountAsync(a, ct)));
    }

    public void Pause()  => _paused = true;
    public void Resume() => _paused = false;

    private async Task SyncAccountAsync(AccountModel account, CancellationToken ct)
    {
        var sem = GetLock(account.Id);
        if (!await sem.WaitAsync(0, ct)) return; // already syncing this account — skip
        try
        {
            SyncingAccount?.Invoke(account.Email);
            var result = await _imap.SyncAsync(account, ct,
                msg => AccountProgress?.Invoke(account.Email, msg));
            if (result.Downloaded > 0)
                NewMailArrived?.Invoke(account, result.Downloaded);
        }
        catch { }
        finally { sem.Release(); }
    }

    private SemaphoreSlim GetLock(int accountId)
    {
        lock (_accountLocks)
        {
            if (!_accountLocks.TryGetValue(accountId, out var sem))
                _accountLocks[accountId] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }
}
