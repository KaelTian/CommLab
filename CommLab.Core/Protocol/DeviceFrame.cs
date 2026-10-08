using Serilog;

namespace CommLab.Core.Protocol
{
    public class DeviceFrame
    {
        private static readonly ILogger Logger = Log.ForContext<DeviceFrame>();

        public const byte Head0 = 0xAA;
        public const byte Head1 = 0x55;

        public const byte CmdHeartbeat = 0x01;
        public const byte CmdHeartbeatAck = 0x81;
        public const byte CmdReadRegister = 0x02;
        public const byte CmdReadRegisterAck = 0x82;
        public const byte CmdError = 0x7F;

        /// <summary>payload 长度上限，超出即视为假帧头（按协议实际最大值调整）</summary>
        public const int MaxPayloadLength = 1024;

        public byte Cmd { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            int len = Payload.Length;
            var buf = new byte[6 + len];
            buf[0] = Head0;
            buf[1] = Head1;
            buf[2] = Cmd;
            buf[3] = (byte)(len >> 8);
            buf[4] = (byte)(len & 0xFF);
            Array.Copy(Payload, 0, buf, 5, len);

            byte crc = 0;
            for (int i = 2; i < 5 + len; i++) crc ^= buf[i];
            buf[5 + len] = crc;
            return buf;
        }
        /// <summary>
        /// 从缓冲区尝试解析一帧，处理半包/粘包/坏帧，返回 false 表示数据不够
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="frame"></param>
        /// <returns></returns>
        public static bool TryParse(List<byte> buffer, out DeviceFrame? frame)
        {
            frame = null;

            while (true)
            {
                // ① 找帧头
                int headIdx = -1;
                for (int i = 0; i + 1 < buffer.Count; i++)
                {
                    if (buffer[i] == Head0 && buffer[i + 1] == Head1)
                    {
                        headIdx = i;
                        break;
                    }
                }
                if (headIdx < 0)
                {
                    // 没有帧头：清掉垃圾字节，但保留最后 1 字节（它可能是下一个帧头的首字节）
                    if (buffer.Count > 1) buffer.RemoveRange(0, buffer.Count - 1);
                    return false;
                }
                if (headIdx > 0) buffer.RemoveRange(0, headIdx); // 丢弃帧头前的垃圾

                // ② 头已就位，长度够不够
                if (buffer.Count < 6) return false; // 2头+1cmd+2len+1crc
                int len = (buffer[3] << 8) | buffer[4];
                if (len > MaxPayloadLength)
                {
                    Logger.Warning("长度字段 {Len} 超出上限，按假帧头处理", len);
                    buffer.RemoveAt(0);
                    continue;                                    // 否则会一直等一个不存在的长度
                }
                int total = 6 + len;
                if (buffer.Count < total) return false; // 半包，等更多数据

                // ③ 校验
                byte crc = 0;
                for (int i = 2; i < total - 1; i++) crc ^= buffer[i];
                if (crc != buffer[total - 1])
                {
                    Logger.Warning("CRC 校验失败，丢弃假帧头继续扫描");
                    buffer.RemoveAt(0);
                    continue;                                    // 可能是帧头恰好出现在数据里
                }

                frame = new DeviceFrame
                {
                    Cmd = buffer[2],
                    Payload = buffer.GetRange(5, len).ToArray()
                };
                buffer.RemoveRange(0, total);
                return true;
            }
        }
    }
}
