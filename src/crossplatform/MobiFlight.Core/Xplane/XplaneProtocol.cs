using System.Buffers.Binary;
using System.Text;

namespace MobiFlight.Core.Xplane;

/// <summary>
/// Builds and parses the X-Plane UDP datagrams MobiFlight needs.
/// </summary>
/// <remarks>
/// These are pure functions with no socket involved so they can be unit tested directly.
/// The layouts are fixed by X-Plane and identical on every platform X-Plane runs on.
/// </remarks>
public static class XplaneProtocol
{
    /// <summary>X-Plane always listens on this UDP port.</summary>
    public const int DefaultPort = 49000;

    /// <summary>Total length of an RREF request. X-Plane rejects anything else.</summary>
    public const int RrefRequestLength = 413;

    /// <summary>Total length of a DREF (write) datagram.</summary>
    public const int DrefLength = 509;

    /// <summary>Bytes available for the dataref path inside an RREF request.</summary>
    public const int RrefPathLength = RrefRequestLength - 13;

    /// <summary>Bytes available for the dataref path inside a DREF datagram.</summary>
    public const int DrefPathLength = DrefLength - 9;

    /// <summary>
    /// Builds a dataref subscription request. A <paramref name="frequency"/> of 0 cancels an
    /// existing subscription.
    /// </summary>
    public static byte[] BuildDataRefSubscription(string dataRef, int frequency, int id)
    {
        ArgumentNullException.ThrowIfNull(dataRef);

        var datagram = new byte[RrefRequestLength];

        // "RREF" followed by a null byte.
        Encoding.ASCII.GetBytes("RREF", datagram.AsSpan(0, 4));

        BinaryPrimitives.WriteInt32LittleEndian(datagram.AsSpan(5, 4), frequency);
        BinaryPrimitives.WriteInt32LittleEndian(datagram.AsSpan(9, 4), id);

        WriteNullPaddedPath(dataRef, datagram.AsSpan(13, RrefPathLength));

        return datagram;
    }

    /// <summary>
    /// Builds a datagram that writes a float value into a dataref.
    /// </summary>
    public static byte[] BuildDataRefWrite(string dataRef, float value)
    {
        ArgumentNullException.ThrowIfNull(dataRef);

        var datagram = new byte[DrefLength];

        Encoding.ASCII.GetBytes("DREF", datagram.AsSpan(0, 4));

        BinaryPrimitives.WriteSingleLittleEndian(datagram.AsSpan(5, 4), value);

        WriteNullPaddedPath(dataRef, datagram.AsSpan(9, DrefPathLength));

        return datagram;
    }

    /// <summary>
    /// Builds a command datagram. Unlike the others this one is variable length.
    /// </summary>
    public static byte[] BuildCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var path = Encoding.ASCII.GetBytes(command);
        var datagram = new byte[5 + path.Length];

        Encoding.ASCII.GetBytes("CMND", datagram.AsSpan(0, 4));
        path.CopyTo(datagram.AsSpan(5));

        return datagram;
    }

    /// <summary>
    /// Reads the (id, value) pairs out of an RREF reply.
    /// </summary>
    /// <remarks>
    /// X-Plane packs as many values into one datagram as fit, so a single packet routinely carries
    /// several datarefs. Anything that is not an RREF reply is ignored.
    /// </remarks>
    public static IReadOnlyList<XplaneDataRefValue> ParseDataRefResponse(ReadOnlySpan<byte> buffer)
    {
        if (!IsDataRefResponse(buffer)) return Array.Empty<XplaneDataRefValue>();

        var results = new List<XplaneDataRefValue>();

        // Skip the 4 byte header plus its null terminator.
        var position = 5;

        while (position + 8 <= buffer.Length)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(position, 4));
            var value = BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(position + 4, 4));
            position += 8;

            results.Add(new XplaneDataRefValue(id, value));
        }

        return results;
    }

    /// <summary>
    /// True when the datagram is a dataref reply from X-Plane.
    /// </summary>
    public static bool IsDataRefResponse(ReadOnlySpan<byte> buffer)
    {
        return buffer.Length >= 5
            && buffer[0] == (byte)'R'
            && buffer[1] == (byte)'R'
            && buffer[2] == (byte)'E'
            && buffer[3] == (byte)'F';
    }

    /// <summary>
    /// Copies an ASCII path into a fixed size, null padded field, truncating if necessary so the
    /// terminator always survives.
    /// </summary>
    private static void WriteNullPaddedPath(string path, Span<byte> target)
    {
        var encoded = Encoding.ASCII.GetBytes(path);

        // Leave at least one byte for the null terminator.
        var length = Math.Min(encoded.Length, target.Length - 1);
        encoded.AsSpan(0, length).CopyTo(target);
    }
}

/// <summary>
/// A single value as reported by X-Plane, still identified by its subscription id.
/// </summary>
public readonly record struct XplaneDataRefValue(int Id, float Value);
