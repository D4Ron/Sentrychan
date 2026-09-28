using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using System;
using System.Linq;
using System.Security.Cryptography;

namespace Sentrychan.UI.Services;

/// <summary>
/// The user's own unlock code for secret mode, stored only as a salted PBKDF2 hash.
///
/// Until one is set, the built-in code still works — but that code is in the public source,
/// so anyone can read it. Setting a PIN replaces it for good.
/// </summary>
public static class SecretPin
{
    private const string ConfigKey = "UiLockHash";
    private const int Iterations = 200_000;

    private static AppDbContext? Db() =>
        App.Services?.GetService<IDbContextFactory<AppDbContext>>()?.CreateDbContext();

    private static string? Stored()
    {
        using var db = Db();
        return db?.AppConfigs.AsNoTracking().FirstOrDefault(c => c.Key == ConfigKey)?.Value;
    }

    public static bool IsSet => !string.IsNullOrEmpty(Stored());

    public static bool Verify(string pin)
    {
        var parts = Stored()?.Split('$');
        if (parts is not { Length: 3 } || !int.TryParse(parts[0], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    public static void Set(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var value = $"{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        using var db = Db();
        if (db == null) return;
        var row = db.AppConfigs.FirstOrDefault(c => c.Key == ConfigKey);
        if (row == null) db.AppConfigs.Add(new AppConfig { Key = ConfigKey, Value = value });
        else row.Value = value;
        db.SaveChanges();
    }
}
