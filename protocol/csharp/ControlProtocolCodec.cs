using System.Buffers.Binary;

namespace ScreenMirror.Protocol;

/// <summary>
/// 控制协议二进制编解码器
/// 格式: [1字节Type] [2字节PayloadLength大端] [N字节Payload]
/// </summary>
public static class ControlProtocolCodec
{
    public const int HeaderSize = 5;   // Type(1) + Length(4)
    public const int MaxPayloadSize = 4 * 1024 * 1024;  // 4MB — 容纳超大 I 帧，使用 4 字节长度字段

    /// <summary>
    /// 编码消息为字节数组
    /// </summary>
    public static byte[] Encode(ControlMessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentException($"Payload too large: {payload.Length} > {MaxPayloadSize}");

        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1, 4), (uint)payload.Length);

        if (payload.Length > 0)
            payload.CopyTo(buffer.AsSpan(HeaderSize));

        return buffer;
    }

    /// <summary>
    /// 编码空 payload 消息
    /// </summary>
    public static byte[] Encode(ControlMessageType type)
    {
        return Encode(type, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// 尝试解码消息头，返回 (Type, PayloadLength)
    /// </summary>
    public static bool TryDecodeHeader(ReadOnlySpan<byte> data, out ControlMessageType type, out int payloadLength)
    {
        type = ControlMessageType.Error;
        payloadLength = 0;

        if (data.Length < HeaderSize)
            return false;

        type = (ControlMessageType)data[0];
        payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4));
        return true;
    }

    /// <summary>
    /// 获取完整消息所需的总字节数
    /// </summary>
    public static int GetMessageSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize) return -1;
        int payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(1, 4));
        return HeaderSize + payloadLength;
    }

    // ============================================================
    // 各消息类型的 Payload 编解码
    // ============================================================

    public static byte[] EncodeTouchEvent(TouchEventData evt)
    {
        var payload = new byte[16];
        payload[0] = (byte)evt.Action;
        payload[1] = (byte)evt.PointerId;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)(evt.X * 10000));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), (ushort)(evt.Y * 10000));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), (ushort)(evt.Pressure * 1000));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8, 8), evt.EventTime);
        return Encode(ControlMessageType.TouchEvent, payload);
    }

    public static TouchEventData DecodeTouchEvent(ReadOnlySpan<byte> payload)
    {
        return new TouchEventData
        {
            Action = (TouchAction)payload[0],
            PointerId = payload[1],
            X = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2)) / 10000f,
            Y = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)) / 10000f,
            Pressure = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2)) / 1000f,
            EventTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(8, 8)),
        };
    }

    public static byte[] EncodeKeyEvent(KeyEventData evt)
    {
        var payload = new byte[17];
        payload[0] = (byte)evt.Action;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1, 4), evt.KeyCode);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5, 4), evt.MetaState);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(9, 8), evt.EventTime);
        return Encode(ControlMessageType.KeyEvent, payload);
    }

    public static KeyEventData DecodeKeyEvent(ReadOnlySpan<byte> payload)
    {
        return new KeyEventData
        {
            Action = (KeyAction)payload[0],
            KeyCode = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(1, 4)),
            MetaState = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(5, 4)),
            EventTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(9, 8)),
        };
    }

    public static byte[] EncodeScreenInfo(ScreenInfoData info)
    {
        var payload = new byte[19];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), info.Width);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), info.Height);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), (ushort)info.Dpi);
        payload[10] = (byte)info.FrameRate;
        payload[11] = (byte)info.Codec;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12, 4), info.MaxBitrateKbps);
        payload[16] = (byte)(info.HasAudio ? 1 : 0);
        payload[17] = (byte)info.AudioCodec;
        payload[18] = (byte)(info.Rotation / 90);
        return Encode(ControlMessageType.ScreenInfo, payload);
    }

    public static ScreenInfoData DecodeScreenInfo(ReadOnlySpan<byte> payload)
    {
        return new ScreenInfoData
        {
            Width = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(0, 4)),
            Height = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4)),
            Dpi = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2)),
            FrameRate = payload[10],
            Codec = (VideoCodecType)payload[11],
            MaxBitrateKbps = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(12, 4)),
            HasAudio = payload[16] == 1,
            AudioCodec = (AudioCodecType)payload[17],
            Rotation = payload[18] * 90,
        };
    }

    public static byte[] EncodeDeviceHello(DeviceHelloData hello)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(hello.DeviceName);
        var payload = new byte[2 + nameBytes.Length];
        payload[0] = (byte)hello.ProtocolVersion;
        payload[1] = (byte)hello.DeviceType;
        Array.Copy(nameBytes, 0, payload, 2, nameBytes.Length);
        return Encode(ControlMessageType.DeviceHello, payload);
    }

    public static DeviceHelloData DecodeDeviceHello(ReadOnlySpan<byte> payload)
    {
        return new DeviceHelloData
        {
            ProtocolVersion = payload[0],
            DeviceType = (DeviceType)payload[1],
            DeviceName = System.Text.Encoding.UTF8.GetString(payload.Slice(2)),
        };
    }

    public static byte[] EncodeHeartbeat(long timestamp)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, timestamp);
        return Encode(ControlMessageType.Heartbeat, payload);
    }

    public static long DecodeHeartbeat(ReadOnlySpan<byte> payload)
    {
        return BinaryPrimitives.ReadInt64BigEndian(payload);
    }

    public static byte[] EncodeHeartbeatAck(long timestamp)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, timestamp);
        return Encode(ControlMessageType.HeartbeatAck, payload);
    }

    public static long DecodeHeartbeatAck(ReadOnlySpan<byte> payload)
    {
        return BinaryPrimitives.ReadInt64BigEndian(payload);
    }

    public static byte[] EncodeMouseEvent(MouseEventData evt)
    {
        var payload = new byte[6];
        payload[0] = (byte)evt.Action;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1, 2), (ushort)(evt.X * 10000));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3, 2), (ushort)(evt.Y * 10000));
        payload[5] = (byte)evt.Button;
        return Encode(ControlMessageType.MouseEvent, payload);
    }

    public static MouseEventData DecodeMouseEvent(ReadOnlySpan<byte> payload)
    {
        return new MouseEventData
        {
            Action = (MouseAction)payload[0],
            X = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2)) / 10000f,
            Y = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2)) / 10000f,
            Button = payload[5],
        };
    }

    public static byte[] EncodeScrollEvent(ScrollEventData evt)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)(evt.X * 10000));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)(evt.Y * 10000));
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(4, 2), (short)(evt.HScroll * 100));
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(6, 2), (short)(evt.VScroll * 100));
        return Encode(ControlMessageType.ScrollEvent, payload);
    }

    public static ScrollEventData DecodeScrollEvent(ReadOnlySpan<byte> payload)
    {
        return new ScrollEventData
        {
            X = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(0, 2)) / 10000f,
            Y = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2)) / 10000f,
            HScroll = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(4, 2)) / 100f,
            VScroll = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(6, 2)) / 100f,
        };
    }

    public static byte[] EncodeError(int code, string message)
    {
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var payload = new byte[4 + msgBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), code);
        Array.Copy(msgBytes, 0, payload, 4, msgBytes.Length);
        return Encode(ControlMessageType.Error, payload);
    }

    public static ErrorData DecodeError(ReadOnlySpan<byte> payload)
    {
        return new ErrorData
        {
            ErrorCode = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(0, 4)),
            Message = System.Text.Encoding.UTF8.GetString(payload.Slice(4)),
        };
    }
}
