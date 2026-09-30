using Foundation;
using Security;
using Trenal.Core;

namespace Trenal.iOS;

/// <summary>Secrets (git tokens) in the iOS Keychain, readable only by this app on this device.</summary>
sealed class KeychainSecretStore : ISecretStore
{
    const string Service = "dev.trenal.secrets";

    static SecRecord Query(string key) => new(SecKind.GenericPassword) { Service = Service, Account = key };

    public string? Get(string key)
    {
        var match = SecKeyChain.QueryAsRecord(Query(key), out var status);
        return status == SecStatusCode.Success && match?.ValueData is { } data
            ? NSString.FromData(data, NSStringEncoding.UTF8)?.ToString()
            : null;
    }

    public void Set(string key, string value)
    {
        Remove(key);
        var record = Query(key);
        record.ValueData = NSData.FromString(value, NSStringEncoding.UTF8);
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        SecKeyChain.Add(record);
    }

    public void Remove(string key) => SecKeyChain.Remove(Query(key));
}
