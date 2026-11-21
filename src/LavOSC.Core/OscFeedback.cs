using System.Buffers.Binary;
using System.Text;

namespace Laveyana.LavOSC.Core;

/// <summary>The last input that affected this feedback address, with the values reflected by its sender.</summary>
public sealed record OscFeedbackOrigin(string IpAddress, long Timestamp, IReadOnlyList<object?> Arguments, bool SuppressEcho = true);

public sealed record OscFeedback(string Name, IReadOnlyList<object?> Arguments)
{
    /// <summary>Sends on /module/name instead of /module/feedback/name.</summary>
    public bool UseCommandAddress { get; init; }

    /// <summary>Baseline assumed for a new recipient. Null sends the current value on first observation.</summary>
    public IReadOnlyList<object?>? InitialArguments { get; init; }

    /// <summary>Set after accepting a client update. Keep the input when a later local change permits feedback to that client.</summary>
    public OscFeedbackOrigin? Origin { get; init; }

    public string GetAddress(string module)
    {
        OscModuleRegistration.ValidateSegment(module, nameof(module));
        OscModuleRegistration.ValidateSegment(Name, nameof(Name));
        return UseCommandAddress ? $"/{module}/{Name}" : $"/{module}/feedback/{Name}";
    }

    public byte[] Encode(string module)
    {
        string address = GetAddress(module);
        var tags = new StringBuilder(",");
        foreach (object? argument in Arguments)
            tags.Append(argument switch
            {
                int => 'i', long => 'h', float => 'f', double => 'd', string => 's',
                true => 'T', false => 'F', null => 'N',
                _ => throw new ArgumentException($"Неподдерживаемый тип OSC: {argument.GetType().Name}.", nameof(Arguments))
            });

        using var stream = new MemoryStream();
        WriteString(stream, address);
        WriteString(stream, tags.ToString());
        Span<byte> bytes = stackalloc byte[8];
        foreach (object? argument in Arguments)
        {
            switch (argument)
            {
                case int number:
                    BinaryPrimitives.WriteInt32BigEndian(bytes, number);
                    stream.Write(bytes[..4]);
                    break;
                case long number:
                    BinaryPrimitives.WriteInt64BigEndian(bytes, number);
                    stream.Write(bytes);
                    break;
                case float number:
                    BinaryPrimitives.WriteSingleBigEndian(bytes, number);
                    stream.Write(bytes[..4]);
                    break;
                case double number:
                    BinaryPrimitives.WriteDoubleBigEndian(bytes, number);
                    stream.Write(bytes);
                    break;
                case string text:
                    WriteString(stream, text);
                    break;
            }
        }

        if (stream.Length > 65507)
            throw new ArgumentException("Сообщение OSC не помещается в дейтаграмму UDP.", nameof(Arguments));
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Строка OSC не может содержать нулевой символ.", nameof(value));
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        stream.Write(encoded);
        for (int remaining = 4 - encoded.Length % 4; remaining > 0; remaining--)
            stream.WriteByte(0);
    }
}

public interface IOscFeedbackSource
{
    // Called on a background thread. Use one address per independent value and return stable arguments.
    IReadOnlyList<OscFeedback> GetFeedback();
}
