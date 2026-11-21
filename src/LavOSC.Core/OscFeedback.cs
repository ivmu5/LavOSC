using System.Buffers.Binary;
using System.Text;

namespace Laveyana.LavOSC.Core;

public sealed record OscFeedback(string Name, IReadOnlyList<object?> Arguments)
{
    /// <summary>Sends on /module/name instead of /module/feedback/name.</summary>
    public bool UseCommandAddress { get; init; }

    public byte[] Encode(string module)
    {
        OscModuleRegistration.ValidateSegment(module, nameof(module));
        OscModuleRegistration.ValidateSegment(Name, nameof(Name));
        var tags = new StringBuilder(",");
        foreach (object? argument in Arguments)
            tags.Append(argument switch
            {
                int => 'i', long => 'h', float => 'f', double => 'd', string => 's',
                true => 'T', false => 'F', null => 'N',
                _ => throw new ArgumentException($"Неподдерживаемый тип OSC: {argument.GetType().Name}.", nameof(Arguments))
            });

        using var stream = new MemoryStream();
        WriteString(stream, UseCommandAddress ? $"/{module}/{Name}" : $"/{module}/feedback/{Name}");
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
    // Called on a background thread. Return a snapshot that remains stable while it is encoded.
    IReadOnlyList<OscFeedback> GetFeedback();
}
