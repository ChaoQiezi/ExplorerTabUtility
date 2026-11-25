using System;
using System.Text.Json.Serialization;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Models;

public class WindowRecord(string location, nint handle = 0, string[]? selectedItems = null, string name = "", bool restore = false, long closedAtTicks = 0)
{
    [JsonConverter(typeof(IntPtrConverter))]
    public nint Handle { get; set; } = handle;
    public string Name { get; set; } = name;
    public string Location { get; set; } = location;
    public string[]? SelectedItems { get; set; } = selectedItems;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Restore { get; set; } = restore;

    /// <summary>
    /// Monotonic high-resolution timestamp (Stopwatch ticks) set only when closed/recorded in the current session.
    /// Deserialized history records keep this at 0 so they are never mistaken for a freshly detached tab.
    /// </summary>
    [JsonIgnore]
    public long ClosedAtTicks { get; set; } = closedAtTicks;

    [JsonIgnore]
    public string DisplayLocation
    {
        get
        {
            var raw = Location.Replace(@"file:\\\", "");
            try
            {
                return Uri.UnescapeDataString(raw);
            }
            catch
            {
                return raw;
            }
        }
    }

    [JsonConstructor]
    private WindowRecord() : this(string.Empty)
    {
    }
}
