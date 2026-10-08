using CommLab.Core.Protocol;
using Xunit;

namespace CommLab.Core.Tests.Protocol;

/// <summary>
/// DeviceFrame.TryParse 的粘包 / 半包 / 坏帧恢复行为验证。
/// 约定：TryParse 每次只解出「一帧」，调用方需要循环调用直到返回 false。
/// </summary>
public class DeviceFrameTests
{
    private static DeviceFrame NewFrame(byte cmd, params byte[] payload) =>
        new() { Cmd = cmd, Payload = payload };

    // ---------- 基线 ----------

    [Fact]
    public void Serialize_ThenTryParse_RoundTrips()
    {
        var original = NewFrame(DeviceFrame.CmdReadRegisterAck, 0x01, 0x02, 0x03);

        var buffer = new List<byte>(original.Serialize());
        bool ok = DeviceFrame.TryParse(buffer, out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal(original.Cmd, parsed!.Cmd);
        Assert.Equal(original.Payload, parsed.Payload);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_HandlesEmptyPayload()
    {
        var buffer = new List<byte>(NewFrame(DeviceFrame.CmdHeartbeat).Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var parsed));
        Assert.Equal(DeviceFrame.CmdHeartbeat, parsed!.Cmd);
        Assert.Empty(parsed.Payload);
        Assert.Empty(buffer);
    }

    // ---------- 半包 ----------

    [Fact]
    public void TryParse_HalfPacket_SplitInTwoChunks()
    {
        var wire = NewFrame(DeviceFrame.CmdReadRegister, 0x10, 0x20, 0x30, 0x40).Serialize();
        int half = wire.Length / 2;

        var buffer = new List<byte>();
        buffer.AddRange(wire.Take(half));

        Assert.False(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Null(frame);
        Assert.Equal(half, buffer.Count); // 半包阶段一个字节都不能丢

        buffer.AddRange(wire.Skip(half));

        Assert.True(DeviceFrame.TryParse(buffer, out frame));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, frame!.Payload);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_HalfPacket_ByteByByte_ResolvesOnLastByte()
    {
        var wire = NewFrame(0x33, 0xAB, 0xCD).Serialize();

        var buffer = new List<byte>();
        var results = new List<bool>();

        foreach (byte b in wire)
        {
            buffer.Add(b);
            results.Add(DeviceFrame.TryParse(buffer, out _));
        }

        Assert.Equal(wire.Length - 1, results.Count(r => !r)); // 前面全部 false
        Assert.True(results[^1]);                              // 只有最后一字节凑齐
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_HalfPacket_SplitInsideLengthField()
    {
        // 最刁钻的断点：len 只到了一半（buffer.Count == 4）
        var wire = NewFrame(0x01, 0x11, 0x22).Serialize();

        var buffer = new List<byte>(wire.Take(4));
        Assert.False(DeviceFrame.TryParse(buffer, out _));
        Assert.Equal(4, buffer.Count);

        buffer.AddRange(wire.Skip(4));
        Assert.True(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Equal(new byte[] { 0x11, 0x22 }, frame!.Payload);
    }

    [Fact]
    public void TryParse_TrailingHeadOnly_KeepsSingleByte()
    {
        // 缓冲区里只剩帧头首字节，必须保留等下一字节
        var buffer = new List<byte> { 0x99, 0xAA };

        Assert.False(DeviceFrame.TryParse(buffer, out _));
        Assert.Equal(new List<byte> { 0xAA }, buffer);
    }

    // ---------- 粘包 ----------

    [Fact]
    public void TryParse_StickyPackets_TwoFramesInOneBuffer()
    {
        var a = NewFrame(DeviceFrame.CmdHeartbeat);
        var b = NewFrame(DeviceFrame.CmdReadRegisterAck, 0xAA, 0x55);

        var buffer = new List<byte>();
        buffer.AddRange(a.Serialize());
        buffer.AddRange(b.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var first));
        Assert.Equal(a.Cmd, first!.Cmd);

        Assert.True(DeviceFrame.TryParse(buffer, out var second));
        Assert.Equal(b.Cmd, second!.Cmd);
        Assert.Equal(b.Payload, second.Payload);

        Assert.False(DeviceFrame.TryParse(buffer, out _)); // 第三帧不存在
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_DrainsUntilBufferEmpty()
    {
        var buffer = new List<byte>();
        for (byte i = 0; i < 20; i++)
            buffer.AddRange(NewFrame(i, i, (byte)(i + 1)).Serialize());

        int count = 0;
        var cmds = new List<byte>();
        while (DeviceFrame.TryParse(buffer, out var f))
        {
            cmds.Add(f!.Cmd);
            count++;
        }

        Assert.Equal(20, count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), cmds);
        Assert.Empty(buffer);
    }

    // ---------- 垃圾字节 ----------

    [Fact]
    public void TryParse_SkipsLeadingGarbage()
    {
        var wire = NewFrame(0x07, 0xDE, 0xAD).Serialize();

        var buffer = new List<byte> { 0x00, 0x00, 0x00, 0xFF, 0x12 };
        buffer.AddRange(wire);

        Assert.True(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Equal(0x07, frame!.Cmd);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, frame.Payload);
    }

    [Fact]
    public void TryParse_SkipsZeroBytesBetweenFrames()
    {
        var a = NewFrame(0x01);
        var b = NewFrame(0x02, 0x77);

        var buffer = new List<byte>();
        buffer.AddRange(a.Serialize());
        buffer.AddRange(new byte[] { 0x00, 0x00, 0x00 }); // 中间塞 0x00 垃圾
        buffer.AddRange(b.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var first));
        Assert.Equal(0x01, first!.Cmd);

        Assert.True(DeviceFrame.TryParse(buffer, out var second));
        Assert.Equal(0x02, second!.Cmd);
        Assert.Equal(new byte[] { 0x77 }, second.Payload);
        Assert.Empty(buffer);
    }

    // ---------- 假帧头（数据里恰好出现 AA 55） ----------

    [Fact]
    public void TryParse_PayloadContainingHeadBytes_IsNotMisparsed()
    {
        // payload 里刻意塞满帧头字节
        var original = NewFrame(0x42, 0xAA, 0x55, 0xAA, 0x55, 0x01);

        var buffer = new List<byte>(original.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var parsed));
        Assert.Equal(0x42, parsed!.Cmd);
        Assert.Equal(original.Payload, parsed.Payload);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_FakeHeadWithBadCrc_RecoversAndFindsRealFrame()
    {
        // 假帧头：AA 55 7F 00 01 42 00
        //   len = 0x0001 -> total = 7
        //   正确 CRC 应为 0x7F^0x00^0x01^0x42 = 0x3C，这里故意放 0x00
        var fakeHead = new byte[] { 0xAA, 0x55, 0x7F, 0x00, 0x01, 0x42, 0x00 };
        var real = NewFrame(0x88, 0x11, 0x22);

        var buffer = new List<byte>(fakeHead);
        buffer.AddRange(real.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Equal(0x88, frame!.Cmd);
        Assert.Equal(new byte[] { 0x11, 0x22 }, frame.Payload);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_GarbageHeadBetweenFrames_Recovers()
    {
        var a = NewFrame(0x01);
        var b = NewFrame(0x02, 0x99);

        var buffer = new List<byte>();
        buffer.AddRange(a.Serialize());
        // 前导 0x00 + 一个假帧 AA 55 7F 00 01 42 00：
        //   len = 0x0001 -> total = 7（长度够，能走到 CRC 分支）
        //   正确 CRC 应为 0x7F^0x00^0x01^0x42 = 0x3C，这里放 0x00 -> 校验失败后重新扫描
        buffer.AddRange(new byte[] { 0x00, 0x00, 0xAA, 0x55, 0x7F, 0x00, 0x01, 0x42, 0x00, 0x13 });
        buffer.AddRange(b.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var first));
        Assert.Equal(0x01, first!.Cmd);

        Assert.True(DeviceFrame.TryParse(buffer, out var second));
        Assert.Equal(0x02, second!.Cmd);
        Assert.Equal(new byte[] { 0x99 }, second.Payload);
        Assert.Empty(buffer);
    }

    // ---------- 综合压力：分块 + 垃圾 + 粘包 一起上 ----------

    [Fact]
    public void TryParse_Torture_ChunkedFeedWithGarbageAndStickyFrames()
    {
        var frames = new[]
        {
            NewFrame(0x01),
            NewFrame(0x02, 0xAA, 0x55, 0xAA, 0x55),   // payload 里含帧头
            NewFrame(DeviceFrame.CmdReadRegisterAck, 0xFF),
        };

        var stream = new List<byte> { 0x00, 0x13, 0x37 };      // 前导垃圾
        foreach (var f in frames)
        {
            stream.AddRange(new byte[] { 0x00, 0x00 });        // 帧间垃圾
            stream.AddRange(f.Serialize());
        }
        stream.AddRange(new byte[] { 0x00, 0x00, 0x00, 0xAA }); // 尾部垃圾 + 半截帧头

        var buffer = new List<byte>();
        var received = new List<DeviceFrame>();

        // 随机分块喂（固定种子，保证可复现）
        var rng = new Random(20260924);
        int pos = 0;
        while (pos < stream.Count)
        {
            int take = rng.Next(1, 7);
            take = Math.Min(take, stream.Count - pos);
            buffer.AddRange(stream.GetRange(pos, take));
            pos += take;

            while (DeviceFrame.TryParse(buffer, out var f))
                received.Add(f!);
        }

        Assert.Equal(frames.Length, received.Count);
        for (int i = 0; i < frames.Length; i++)
        {
            Assert.Equal(frames[i].Cmd, received[i].Cmd);
            Assert.Equal(frames[i].Payload, received[i].Payload);
        }
        // 尾部只剩半个帧头（0xAA），清理逻辑会保留它等下一字节
        Assert.Equal(new List<byte> { 0xAA }, buffer);
    }

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsFalse()
    {
        var buffer = new List<byte>();
        Assert.False(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Null(frame);
        Assert.Empty(buffer);
    }

    // ---------- 长度上限（防假帧头卡死缓冲区） ----------

    [Fact]
    public void TryParse_FakeHeadWithHugeLength_IsTreatedAsFakeHead()
    {
        // 假帧头 AA 55 01 FF FF -> len = 0xFFFF（远超上限）
        // 没有上限保护时，这里会一直 return false 等一个不存在的 64KB，真实帧被卡在缓冲区里
        var buffer = new List<byte> { 0xAA, 0x55, 0x01, 0xFF, 0xFF };
        var real = NewFrame(0x66, 0x01);
        buffer.AddRange(real.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Equal(0x66, frame!.Cmd);
        Assert.Equal(new byte[] { 0x01 }, frame.Payload);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_LengthJustOverLimit_IsTreatedAsFakeHead()
    {
        int len = DeviceFrame.MaxPayloadLength + 1;
        var buffer = new List<byte> { 0xAA, 0x55, 0x01, (byte)(len >> 8), (byte)(len & 0xFF) };
        var real = NewFrame(0x77, 0x05);
        buffer.AddRange(real.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var frame));
        Assert.Equal(0x77, frame!.Cmd);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryParse_LengthExactlyAtLimit_StillRoundTrips()
    {
        // 边界：正好等于上限的合法帧不能被误杀
        var payload = new byte[DeviceFrame.MaxPayloadLength];
        new Random(1).NextBytes(payload);
        var original = NewFrame(0x55, payload);

        var buffer = new List<byte>(original.Serialize());

        Assert.True(DeviceFrame.TryParse(buffer, out var parsed));
        Assert.Equal(payload, parsed!.Payload);
        Assert.Empty(buffer);
    }
}
