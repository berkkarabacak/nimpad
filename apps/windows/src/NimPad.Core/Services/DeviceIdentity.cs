using NimPad.Core.Ids;
using NimPad.Core.Storage;
using NimPad.Core.Time;

namespace NimPad.Core.Services;

/// <summary>The <c>device</c> row: one ID per installation, created on first use.</summary>
internal static class DeviceIdentity
{
    /// <summary>Returns this installation's device ID, creating the row if it does not exist yet.</summary>
    public static Guid GetOrCreate(WriteContext context, TimeProvider timeProvider, IIdGenerator ids)
    {
        if (context.Scalar("SELECT device_id FROM device ORDER BY created_at, device_id LIMIT 1;") is string existing)
        {
            return Uuid.Parse(existing, "device_id");
        }

        var deviceId = ids.NewGuid();
        context.Execute(
            "INSERT INTO device (device_id, created_at) VALUES ($id, $now);",
            ("$id", deviceId),
            ("$now", Timestamps.Now(timeProvider)));
        return deviceId;
    }
}
