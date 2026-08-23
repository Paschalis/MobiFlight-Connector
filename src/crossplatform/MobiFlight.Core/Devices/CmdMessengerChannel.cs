using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace MobiFlight.Core.Devices;

/// <summary>
/// One decoded CmdMessenger message: a command id followed by its string arguments.
/// </summary>
public sealed record CmdMessage(int CommandId, IReadOnlyList<string> Arguments)
{
    public string Argument(int index) => index < Arguments.Count ? Arguments[index] : string.Empty;

    public int ArgumentAsInt(int index) =>
        int.TryParse(Argument(index), CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>
/// Speaks the CmdMessenger framing used by the MobiFlight firmware over a serial port.
/// </summary>
/// <remarks>
/// Fields are separated by ',', a message ends with ';', and '\' escapes any of those plus itself
/// and NUL. This is a focused reimplementation rather than a reference to the CommandMessenger
/// project, so the portable core stays free of the Windows targeted build.
/// </remarks>
public sealed class CmdMessengerChannel : IDisposable
{
    public const char FieldSeparator = ',';
    public const char CommandSeparator = ';';
    public const char EscapeCharacter = '\\';

    private readonly SerialPort _port;
    private readonly object _sendLock = new();
    private readonly StringBuilder _pending = new();
    private readonly List<string> _fields = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<CmdMessage>> _waiters = new();
    private bool _escaped;
    private bool _disposed;

    /// <summary>Raised for every message that nobody was explicitly waiting for.</summary>
    public event EventHandler<CmdMessage>? MessageReceived;

    /// <summary>Raised when the serial link fails.</summary>
    public event EventHandler<Exception>? Error;

    public CmdMessengerChannel(string portName, int baudRate, bool dtrEnable = true)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 1000,
            // Boards built on the ATmega32u4 reset when DTR is asserted, which is also how the
            // Windows Connector opens them.
            DtrEnable = dtrEnable,
        };

        _port.DataReceived += OnDataReceived;
    }

    public string PortName => _port.PortName;

    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    /// <summary>
    /// Sends a command and does not wait for anything.
    /// </summary>
    public void Send(int commandId, params object[] arguments)
    {
        var message = Build(commandId, arguments);

        lock (_sendLock)
        {
            if (!_port.IsOpen) throw new InvalidOperationException($"Port {_port.PortName} is not open.");

            _port.Write(message);
        }
    }

    /// <summary>
    /// Sends a command and waits for a specific reply.
    /// </summary>
    /// <returns>The reply, or null when it did not arrive in time.</returns>
    public async Task<CmdMessage?> SendAndWaitAsync(
        int commandId,
        int expectedReplyId,
        TimeSpan timeout,
        params object[] arguments)
    {
        var waiter = new TaskCompletionSource<CmdMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[expectedReplyId] = waiter;

        try
        {
            Send(commandId, arguments);

            var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeout)).ConfigureAwait(false);
            return completed == waiter.Task ? waiter.Task.Result : null;
        }
        finally
        {
            _waiters.TryRemove(expectedReplyId, out _);
        }
    }

    /// <summary>
    /// Encodes a command into the wire format, escaping every argument.
    /// </summary>
    internal static string Build(int commandId, params object[] arguments)
    {
        var builder = new StringBuilder();
        builder.Append(commandId.ToString(CultureInfo.InvariantCulture));

        foreach (var argument in arguments)
        {
            builder.Append(FieldSeparator);
            builder.Append(Escape(Format(argument)));
        }

        builder.Append(CommandSeparator);
        return builder.ToString();
    }

    private static string Format(object argument) => argument switch
    {
        null => string.Empty,
        string text => text,
        float single => single.ToString("0.####", CultureInfo.InvariantCulture),
        double whole => whole.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => argument.ToString() ?? string.Empty
    };

    internal static string Escape(string input)
    {
        var builder = new StringBuilder(input.Length);

        foreach (var character in input)
        {
            if (character is EscapeCharacter or FieldSeparator or CommandSeparator or '\0')
            {
                builder.Append(EscapeCharacter);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var available = _port.BytesToRead;
            if (available <= 0) return;

            var buffer = new byte[available];
            var read = _port.Read(buffer, 0, available);

            Consume(Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            if (!_disposed) Error?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Feeds raw characters through the framing state machine.
    /// </summary>
    /// <remarks>
    /// Unescaping and field splitting happen in the same pass on purpose. Splitting afterwards
    /// would be wrong: an escaped separator inside a value is indistinguishable from a real one
    /// once the escape character has been removed.
    /// </remarks>
    internal void Consume(string chunk)
    {
        foreach (var character in chunk)
        {
            if (_escaped)
            {
                _pending.Append(character);
                _escaped = false;
                continue;
            }

            switch (character)
            {
                case EscapeCharacter:
                    _escaped = true;
                    continue;

                case FieldSeparator:
                    _fields.Add(_pending.ToString());
                    _pending.Clear();
                    continue;

                case CommandSeparator:
                    _fields.Add(_pending.ToString());
                    _pending.Clear();
                    Dispatch(_fields);
                    _fields.Clear();
                    continue;

                default:
                    _pending.Append(character);
                    continue;
            }
        }
    }

    private void Dispatch(List<string> fields)
    {
        if (fields.Count == 0) return;

        // Some firmware versions emit newlines between messages; they land in the first field.
        var head = fields[0].Trim('\r', '\n', ' ');
        if (head.Length == 0 && fields.Count == 1) return;

        if (!int.TryParse(head, CultureInfo.InvariantCulture, out var commandId)) return;

        var message = new CmdMessage(commandId, fields.Skip(1).ToList());

        if (_waiters.TryGetValue(commandId, out var waiter) && waiter.TrySetResult(message))
        {
            return;
        }

        MessageReceived?.Invoke(this, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _port.DataReceived -= OnDataReceived;

        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do while tearing down.
        }

        _port.Dispose();
    }
}
