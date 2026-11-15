using System.Globalization;
using Microsoft.Win32;

namespace WindowsReborn.Tweaks;

/// <summary>Lectura del registro (separada para poder probar el motor sin tocar el sistema).</summary>
public interface IRegistryReader
{
    RegValue? Read(RegHive hive, string key, string name);
    bool KeyExists(RegHive hive, string key);
    IReadOnlyList<string> ValueNames(RegHive hive, string key);
}

/// <summary>Escritura de un lote de operaciones; puede pedir elevación para HKLM.</summary>
public interface IRegistryWriter
{
    Task WriteAsync(IReadOnlyList<RegOp> ops, bool createRestorePoint, CancellationToken ct = default);
}

/// <summary>Registro real de Windows (vista de 64 bits).</summary>
public sealed class WindowsRegistry : IRegistryReader
{
    public static WindowsRegistry Instance { get; } = new();

    private static RegistryKey Base(RegHive hive) =>
        RegistryKey.OpenBaseKey(hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public RegValue? Read(RegHive hive, string key, string name)
    {
        try
        {
            using var b = Base(hive);
            using var k = b.OpenSubKey(key);
            if (k is null) return null;
            var raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;
            return k.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => new RegValue(RegKind.DWord, unchecked((uint)(int)raw).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.String or RegistryValueKind.ExpandString => new RegValue(RegKind.String, (string)raw),
                RegistryValueKind.Binary => new RegValue(RegKind.Binary, Convert.ToHexString((byte[])raw)),
                RegistryValueKind.QWord => new RegValue(RegKind.String, raw.ToString()!),
                _ => new RegValue(RegKind.String, raw.ToString() ?? ""),
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public bool KeyExists(RegHive hive, string key)
    {
        try
        {
            using var b = Base(hive);
            using var k = b.OpenSubKey(key);
            return k is not null;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> ValueNames(RegHive hive, string key)
    {
        try
        {
            using var b = Base(hive);
            using var k = b.OpenSubKey(key);
            return k?.GetValueNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Aplica operaciones directamente (sin elevación: falla si no hay permisos).</summary>
    public static void Execute(IEnumerable<RegOp> ops)
    {
        foreach (var op in ops)
        {
            using var b = Base(op.Hive);
            switch (op.Type)
            {
                case RegOpType.SetValue:
                {
                    using var k = b.CreateSubKey(op.Key, writable: true);
                    switch (op.Kind)
                    {
                        case RegKind.DWord:
                            k.SetValue(op.Name, unchecked((int)uint.Parse(op.Data ?? "0", CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                            break;
                        case RegKind.Binary:
                            k.SetValue(op.Name, Convert.FromHexString(op.Data ?? ""), RegistryValueKind.Binary);
                            break;
                        default:
                            k.SetValue(op.Name, op.Data ?? "", RegistryValueKind.String);
                            break;
                    }
                    break;
                }
                case RegOpType.DeleteValue:
                {
                    using var k = b.OpenSubKey(op.Key, writable: true);
                    k?.DeleteValue(op.Name, throwOnMissingValue: false);
                    break;
                }
                case RegOpType.DeleteKey:
                    b.DeleteSubKeyTree(op.Key, throwOnMissingSubKey: false);
                    break;
            }
        }
    }
}

/// <summary>Registro en memoria para pruebas y simulaciones.</summary>
public sealed class MemoryRegistry : IRegistryReader, IRegistryWriter
{
    private readonly Dictionary<string, Dictionary<string, RegValue>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public List<RegOp> Log { get; } = [];
    public int RestorePoints { get; private set; }
    /// <summary>Valores que «Windows» rechaza (simula claves protegidas).</summary>
    public HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegHive hive, string key) => $"{hive}\\{key.TrimEnd('\\')}";

    public void Set(RegHive hive, string key, string name, RegKind kind, string data)
    {
        EnsureKey(hive, key);
        _keys[K(hive, key)][name] = new RegValue(kind, data);
    }

    private void EnsureKey(RegHive hive, string key)
    {
        var parts = key.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var k = K(hive, string.Join('\\', parts[..i]));
            if (!_keys.ContainsKey(k)) _keys[k] = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public RegValue? Read(RegHive hive, string key, string name) =>
        _keys.TryGetValue(K(hive, key), out var values) && values.TryGetValue(name, out var v) ? v : null;

    public bool KeyExists(RegHive hive, string key) => _keys.ContainsKey(K(hive, key));

    public IReadOnlyList<string> ValueNames(RegHive hive, string key) =>
        _keys.TryGetValue(K(hive, key), out var values) ? values.Keys.ToList() : [];

    public Task WriteAsync(IReadOnlyList<RegOp> ops, bool createRestorePoint, CancellationToken ct = default)
    {
        if (createRestorePoint) RestorePoints++;
        foreach (var op in ops)
        {
            Log.Add(op);
            if (Protected.Contains(op.Name)) continue;
            switch (op.Type)
            {
                case RegOpType.SetValue:
                    Set(op.Hive, op.Key, op.Name, op.Kind, op.Data ?? "");
                    break;
                case RegOpType.DeleteValue:
                    if (_keys.TryGetValue(K(op.Hive, op.Key), out var values)) values.Remove(op.Name);
                    break;
                case RegOpType.DeleteKey:
                    var prefix = K(op.Hive, op.Key);
                    foreach (var k in _keys.Keys.Where(k => k.Equals(prefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                        _keys.Remove(k);
                    break;
            }
        }
        return Task.CompletedTask;
    }
}
