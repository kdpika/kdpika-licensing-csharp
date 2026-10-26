using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KDPika.LicenseSdk;

/// <summary>Product-scoped hashes only. Raw hardware identifiers are never persisted or sent.</summary>
public static class HardwareFingerprint
{
    private static readonly string[] Names = { "baseboard", "cpu", "disk", "nic", "machine" };
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Usable(string? value)
    {
        var v = Regex.Replace(value?.Trim().ToLowerInvariant() ?? "", @"\s+", " ");
        return v.Length is 0 or >1024 || new[] { "unknown", "none", "default string", "to be filled by o.e.m.", "system serial number", "not specified", "not applicable" }.Contains(v) || Regex.IsMatch(v, @"\A[0f:\- ]+\z") ? "" : v;
    }
    private static string Combined(IEnumerable<KeyValuePair<string, string>> parts) => Hash(string.Join("\n", parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));
    public static JsonElement FromComponents(string product, IReadOnlyDictionary<string, string> raw, bool? isVirtual = null)
    {
        var salt = Hash("kdpika-hardware-v1|" + product);
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw) if (Names.Contains(pair.Key) && Usable(pair.Value) is string v && v.Length > 0) parts[pair.Key] = Hash(salt + "|" + pair.Key + "|" + v);
        if (!parts.Keys.Any(k => k != "cpu")) throw new CreditOperationException("HARDWARE_UNAVAILABLE");
        return JsonSerializer.SerializeToElement(new { version = 1, hardware_id = Combined(parts), components = parts, is_virtual = isVirtual });
    }
    internal static bool Matches(JsonElement binding, JsonElement current)
    {
        try
        {
            if (binding.GetProperty("version").GetInt32() != 1) return false;
            var saved = binding.GetProperty("components").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            if (saved.Count == 0 || saved.Any(p => !Names.Contains(p.Key) || !Regex.IsMatch(p.Value, "\\A[a-f0-9]{64}\\z")) || binding.GetProperty("hardware_id").GetString() != Combined(saved)) return false;
            var actual = current.GetProperty("components").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            var policy = binding.GetProperty("policy");
            if (policy.GetProperty("mode").GetString() == "strict") return Combined(saved) == Combined(actual);
            if (policy.GetProperty("mode").GetString() != "fuzzy" || !policy.GetProperty("min_matches").TryGetInt32(out var min) || min is <1 or >5) return false;
            foreach (var name in new[] { "baseboard", "cpu" }) if (saved.TryGetValue(name, out var v) && (!actual.TryGetValue(name, out var a) || a != v)) return false;
            return saved.Count(p => actual.TryGetValue(p.Key, out var v) && v == p.Value) >= Math.Min(saved.Count, min);
        }
        catch { return false; }
    }
    private static string Read(string path) { try { using var r = new StreamReader(path); var buffer = new char[65536]; return new string(buffer, 0, r.ReadBlock(buffer, 0, buffer.Length)).Trim(); } catch { return ""; } }
    private static string Run(string executable, params string[] args)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            // Drain both pipes with a hard output cap and deadline; no raw output is logged.
            var output = Task.Run(async () => { var buffer = new char[65537]; var n = 0; while (n < buffer.Length) { var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(n)); if (count == 0) return new string(buffer, 0, n); n += count; } try { process.Kill(true); } catch { } return ""; });
            var errors = Task.Run(async () => { var buffer = new char[1024]; var n = 0; int count; while ((count = await process.StandardError.ReadAsync(buffer)) > 0) { n += count; if (n > 65536) { try { process.Kill(true); } catch { } break; } } });
            if (!process.WaitForExit(4000)) { process.Kill(true); return ""; }
            return process.ExitCode == 0 && output.Wait(500) && errors.Wait(500) ? output.Result.Trim() : "";
        }
        catch { return ""; }
    }
    public static JsonElement Collect(string product)
    {
        var raw = new Dictionary<string, string>(); var model = "";
        if (OperatingSystem.IsWindows())
        {
            const string script = "$ErrorActionPreference='SilentlyContinue';$b=(Get-CimInstance Win32_BaseBoard|Select-Object -First 1).SerialNumber;$c=(Get-CimInstance Win32_Processor|Select-Object -First 1).ProcessorId;$m=(Get-CimInstance Win32_ComputerSystemProduct|Select-Object -First 1).UUID;$d=(Get-Partition -DriveLetter $env:SystemDrive.Substring(0,1)|Get-Disk|Select-Object -First 1).SerialNumber;$n=(Get-NetAdapter -Physical|Where-Object Status -eq 'Up'|Sort-Object InterfaceIndex|Select-Object -First 1).MacAddress;@{baseboard=$b;cpu=$c;machine=$m;disk=$d;nic=$n;model=(Get-CimInstance Win32_ComputerSystem).Model}|ConvertTo-Json -Compress";
            try { using var data = JsonDocument.Parse(Run("powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script)); foreach (var name in Names) if (data.RootElement.TryGetProperty(name, out var v)) raw[name] = v.GetString() ?? ""; if (Usable(raw.GetValueOrDefault("baseboard")).Length > 0) raw.Remove("machine"); model = data.RootElement.GetProperty("model").GetString() ?? ""; } catch { }
        }
        else if (OperatingSystem.IsMacOS())
        {
            model = Run("/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice"); raw["baseboard"] = Regex.Match(model, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"\\n]+)\"").Groups[1].Value;
            raw["cpu"] = Run("/usr/sbin/sysctl", "-n", "machdep.cpu.brand_string"); if (raw["cpu"].Length == 0) raw["cpu"] = Run("/usr/sbin/sysctl", "-n", "hw.model");
            raw["nic"] = Regex.Match(Run("/sbin/ifconfig", "en0"), @"\bether\s+([0-9a-fA-F:]{17})").Groups[1].Value;
        }
        else if (OperatingSystem.IsLinux())
        {
            raw["baseboard"] = Read("/sys/class/dmi/id/board_serial"); if (raw["baseboard"].Length == 0) raw["baseboard"] = Read("/sys/class/dmi/id/product_uuid");
            if (Usable(raw["baseboard"]).Length == 0) { raw["machine"] = Read("/etc/machine-id"); if (raw["machine"].Length == 0) raw["machine"] = Read("/var/lib/dbus/machine-id"); }
            raw["cpu"] = Regex.Match(Read("/proc/cpuinfo"), @"^(?:model name|Hardware|Serial)\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value;
            if (raw["cpu"].Length == 0) raw["cpu"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            if (Directory.Exists("/sys/class/block")) foreach (var path in Directory.GetDirectories("/sys/class/block").OrderBy(p => p, StringComparer.Ordinal)) { if (Regex.IsMatch(Path.GetFileName(path), @"^(loop|ram|dm-|sr)") || File.Exists(path + "/partition")) continue; var s = Read(path + "/device/serial"); if (Usable(s).Length > 0) { raw["disk"] = s; break; } }
            if (Directory.Exists("/sys/class/net")) foreach (var path in Directory.GetDirectories("/sys/class/net").OrderBy(p => p, StringComparer.Ordinal)) { if (!Directory.Exists(path + "/device") || Read(path + "/operstate") != "up") continue; var s = Read(path + "/address"); if (Usable(s).Length > 0) { raw["nic"] = s; break; } }
            model = Read("/sys/class/dmi/id/product_name") + " " + Read("/sys/class/dmi/id/sys_vendor");
        }
        else throw new CreditOperationException("HARDWARE_UNAVAILABLE");
        return FromComponents(product, raw, string.IsNullOrWhiteSpace(model) ? null : Regex.IsMatch(model, "vmware|virtualbox|virtual machine|qemu|kvm|parallels|xen", RegexOptions.IgnoreCase));
    }
}
