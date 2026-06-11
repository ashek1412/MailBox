using MailBox.Models;

namespace MailBox.Services;

public class BackgroundSyncService
{
    private readonly AccountRepository _accounts;
    private readonly ImapSyncService   _imap;

    public event Action<AccountModel, int>? NewMailArrived;

    public BackgroundSyncService(AccountRepository accounts, ImapSyncService imap)
    {
        _accounts = accounts;
        _imap     = imap;
    }

    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        var accounts = _accounts.GetAll();
        foreach (var account in accounts)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var result = await _imap.SyncAsync(account, ct);
                if (result.Downloaded > 0)
                    NewMailArrived?.Invoke(account, result.Downloaded);
            }
            catch { }
        }
    }
}
