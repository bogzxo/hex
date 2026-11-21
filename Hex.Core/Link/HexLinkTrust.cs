using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>A device this machine has paired with, as the list of known devices keeps it.</summary>
/// <param name="Device">Its id (the pin) and what it last said it's called, runs and is.</param>
/// <param name="Rights">What it may do on this machine, as the person here said.</param>
/// <param name="Added">When it was paired, UTC.</param>
/// <param name="Seen">When it last linked, UTC, Added until it has.</param>
/// <param name="Address">The address it last linked from or was dialled at, empty until it has.</param>
/// <param name="Port">The port it listens on, as last dialled or announced, 0 when not known.</param>
internal sealed record HexLinkKnown(HexLinkDevice Device, HexLinkRights Rights, DateTime Added, DateTime Seen, string Address, int Port);

/// <summary>
/// The devices this machine knows, kept in <c>&lt;folder&gt;/devices.json</c> (docs/hex-link-plan.md, 3.3 and 3.6). A
/// device is in it once both people confirmed the pairing, and out of it the moment it's forgotten; its pin being in it
/// is the whole of what lets it link. Nothing secret is kept here, only pins, names and what each may do, so it's an
/// ordinary file written whole and at once like McpDiscovery's.
/// <para>
/// Every method may be called from any thread, the listener's, a link's or the editor's. <see cref="Changed"/> says
/// which device changed, after the file was written, on the thread that changed it.
/// </para>
/// </summary>
internal sealed class HexLinkTrust
{
    /// <summary>The file in the folder.</summary>
    public const string FILE = "devices.json";

    private readonly string path;
    private readonly Lock gate = new();
    private readonly Dictionary<string, HexLinkKnown> devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A device was added, changed or forgotten, its id.</summary>
    public event Action<string>? Changed;

    /// <summary>The known devices of the folder, read now, an empty list when there's no file or it can't be read.</summary>
    public HexLinkTrust(string folder)
    {
        HexLinkIdentity.MakeFolder(folder);
        path = Path.Combine(folder, FILE);
        foreach (HexLinkKnown known in Read(path))
            devices[known.Device.Id] = known;
    }

    /// <summary>Every known device, as it was now, by name.</summary>
    public IReadOnlyList<HexLinkKnown> Known
    {
        get
        {
            lock (gate)
                return [.. devices.Values.OrderBy(known => known.Device.Name, StringComparer.OrdinalIgnoreCase).ThenBy(known => known.Device.Id)];
        }
    }

    /// <summary>The device with that id, null when it isn't known.</summary>
    public HexLinkKnown? Find(string id)
    {
        lock (gate)
            return devices.GetValueOrDefault(id);
    }

    /// <summary>Adds a device, or pairs again with one already known, which takes the new rights and keeps when it was first added.</summary>
    public HexLinkKnown Add(HexLinkDevice device, HexLinkRights rights, string address = "", int port = 0)
    {
        HexLinkKnown added;
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;
            DateTime first = devices.TryGetValue(device.Id, out HexLinkKnown? was) ? was.Added : now;
            added = new HexLinkKnown(device, rights, first, now, address, port);
            devices[device.Id] = added;
            Save();
        }

        Changed?.Invoke(device.Id);
        return added;
    }

    /// <summary>Says what a device may do here from now on, false when it isn't known.</summary>
    public bool SetRights(string id, HexLinkRights rights) => Change(id, known => known with { Rights = rights });

    /// <summary>Calls a device something else here, false when it isn't known.</summary>
    public bool Rename(string id, string name) => Change(id, known => known with { Device = known.Device with { Name = name } });

    /// <summary>
    /// A device linked, what it said of itself and where it was, false when it isn't known. Its name, OS, processor and
    /// version are taken as said (they're only ever shown), its id never changes.
    /// </summary>
    public bool Seen(string id, HexLinkDevice said, string address, int port = 0) => Change(id, known => known with
    {
        Device = known.Device with
        {
            Name = said.Name.Length > 0 ? said.Name : known.Device.Name,
            Os = said.Os.Length > 0 ? said.Os : known.Device.Os,
            Arch = said.Arch.Length > 0 ? said.Arch : known.Device.Arch,
            Version = said.Version.Length > 0 ? said.Version : known.Device.Version
        },
        Seen = DateTime.UtcNow,
        Address = address.Length > 0 ? address : known.Address,
        Port = port > 0 ? port : known.Port
    });

    /// <summary>Forgets a device, its pin gone so it can't link again without pairing, false when it wasn't known.</summary>
    public bool Forget(string id)
    {
        lock (gate)
        {
            if (!devices.Remove(id))
                return false;
            Save();
        }

        Changed?.Invoke(id);
        return true;
    }

    private bool Change(string id, Func<HexLinkKnown, HexLinkKnown> change)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out HexLinkKnown? known))
                return false;
            devices[id] = change(known);
            Save();
        }

        Changed?.Invoke(id);
        return true;
    }

    // Under the gate
    private void Save()
    {
        var list = new JsonArray();
        foreach (HexLinkKnown known in devices.Values)
        {
            list.Add((JsonNode)new JsonObject
            {
                ["id"] = known.Device.Id,
                ["name"] = known.Device.Name,
                ["os"] = known.Device.Os,
                ["arch"] = known.Device.Arch,
                ["version"] = known.Device.Version,
                ["rights"] = new JsonArray([.. RightsNames(known.Rights).Select(name => (JsonNode)name)]),
                ["added"] = known.Added.ToString("O"),
                ["seen"] = known.Seen.ToString("O"),
                ["address"] = known.Address,
                ["port"] = known.Port
            });
        }

        var written = new JsonObject { ["version"] = 1, ["devices"] = list };
        HexLinkIdentity.WriteUserOnly(path, System.Text.Encoding.UTF8.GetBytes(written.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    /// <summary>Rights as the words the file keeps them by, view, steer and edit.</summary>
    public static IEnumerable<string> RightsNames(HexLinkRights rights)
    {
        if (rights.HasFlag(HexLinkRights.View))
            yield return "view";
        if (rights.HasFlag(HexLinkRights.Steer))
            yield return "steer";
        if (rights.HasFlag(HexLinkRights.Edit))
            yield return "edit";
        if (rights.HasFlag(HexLinkRights.Predict))
            yield return "predict";
    }

    /// <summary>Rights from those words, the ones it doesn't know left out.</summary>
    public static HexLinkRights RightsOf(IEnumerable<string?> names)
    {
        HexLinkRights rights = HexLinkRights.None;
        foreach (string? name in names)
        {
            rights |= name switch
            {
                "view" => HexLinkRights.View,
                "steer" => HexLinkRights.Steer,
                "edit" => HexLinkRights.Edit,
                "predict" => HexLinkRights.Predict,
                _ => HexLinkRights.None
            };
        }

        return rights;
    }

    private static List<HexLinkKnown> Read(string path)
    {
        var read = new List<HexLinkKnown>();
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["devices"] is not JsonArray list)
                return read;

            foreach (JsonNode? entry in list)
            {
                if (entry is not JsonObject device || Text(device, "id") is not { Length: 64 } id)
                    continue;

                HexLinkRights rights = device["rights"] is JsonArray names ? RightsOf(names.Select(name => name is JsonValue value && value.TryGetValue(out string? word) ? word : null)) : HexLinkRights.None;
                DateTime added = Time(device, "added"), seen = Time(device, "seen");
                int port = device["port"] is JsonValue portValue && portValue.TryGetValue(out int number) ? number : 0;
                read.Add(new HexLinkKnown(new HexLinkDevice(id.ToUpperInvariant(), Text(device, "name") ?? "", Text(device, "os") ?? "", Text(device, "arch") ?? "", Text(device, "version") ?? ""),
                    rights, added, seen, Text(device, "address") ?? "", port));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"[Hex] Link's known devices couldn't be read, {e.Message}");
        }

        return read;
    }

    private static string? Text(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static DateTime Time(JsonObject json, string name) =>
        Text(json, name) is { } text && DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime time) ? time.ToUniversalTime() : DateTime.UtcNow;
}
