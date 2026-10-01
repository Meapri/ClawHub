using HidSharp;

namespace ClawCore.Controller;

/// <summary>
/// Claw 내장 컨트롤러의 벤더 HID 채널. 모든 호출은 직렬화되며 블랙리스트 명령은 <see cref="ControllerProtocol"/>에서 차단된다.
/// 플래시 커밋(0x22)은 의도적으로 지원하지 않는다 — 모든 쓰기는 RAM에만 남고 재부팅 시 원래대로 돌아간다.
/// </summary>
public sealed class ClawController : IDisposable
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromMilliseconds(1000);
    private const int MaxAttempts = 3;

    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly object _lock = new();

    private ClawController(HidDevice device, HidStream stream)
    {
        _device = device;
        _stream = stream;
        _stream.ReadTimeout = 100;
    }

    public string DevicePath => _device.DevicePath;
    public int ProductId => _device.ProductID;

    /// <summary>USB bcdDevice 기반 펌웨어 버전 (예: 0x0131 → "1.31").</summary>
    public string FirmwareVersion
    {
        get
        {
            var hex = _device.ReleaseNumberBcd.ToString("X");
            return hex.Length > 2 ? $"{hex[..^2]}.{hex[^2..]}" : hex;
        }
    }

    public static ClawController? TryOpen()
    {
        foreach (var dev in DeviceList.Local.GetHidDevices(ControllerProtocol.VendorId))
        {
            if (!ControllerProtocol.ProductIds.Contains(dev.ProductID)) continue;
            try
            {
                var desc = dev.GetReportDescriptor();
                if (!desc.OutputReports.Any(r => r.ReportID == ControllerProtocol.ReportId)) continue;
                if (dev.TryOpen(out var stream)) return new ClawController(dev, stream);
            }
            catch (Exception)
            {
                // 디스크립터를 못 읽는 컬렉션은 건너뛴다
            }
        }
        return null;
    }

    public (GamepadMode Mode, MKeysFunction MKeys) ReadMode()
    {
        lock (_lock)
        {
            // 0x27은 모드가 바뀔 때 비동기로도 오므로, 쌓여 있던 옛 0x27을 버린 뒤 요청한다
            var resp = Transact(ControllerProtocol.BuildPacket(ControllerProtocol.CmdReadMode, []),
                r => r[4] == ControllerProtocol.CmdModeAck, drainFirst: true);
            return ((GamepadMode)resp[5], (MKeysFunction)resp[6]);
        }
    }

    public void SwitchMode(GamepadMode mode, MKeysFunction mk)
    {
        lock (_lock)
        {
            _stream.Write(ControllerProtocol.BuildSwitchMode(mode, mk));
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// 모드를 바꾸고 0x26으로 다시 읽어 확인한다 (응답 없는 명령이라 확인이 유일한 성공 신호).
    /// 확인되지 않으면 예외.
    /// </summary>
    public void SwitchModeVerified(GamepadMode mode, MKeysFunction mk)
    {
        lock (_lock)
        {
            SwitchMode(mode, mk);
            for (var i = 0; i < 5; i++)
            {
                if (ReadMode().Mode == mode) return;
                Thread.Sleep(40);
            }
            throw new IOException($"컨트롤러 모드 전환이 확인되지 않음 ({mode})");
        }
    }

    /// <summary>마지막 RAM 쓰기(0x21)에 대한 응답 리포트의 [4] (진단용, 실기 ack 형태 확인).</summary>
    public byte? LastWriteAck { get; private set; }

    /// <summary>
    /// 자이로/가속도 값을 잠깐 받아 본다 (센서 유무 확인·자이로 테스트용). 끝나면 스트림을 끈다.
    /// 값: Gx, Gy, Gz, Ax, Ay, Az (int16 LE).
    /// </summary>
    public List<short[]> SampleMotion(TimeSpan duration)
    {
        var samples = new List<short[]>();
        lock (_lock)
        {
            _stream.Write(ControllerProtocol.BuildPacket(ControllerProtocol.CmdMotionStream, [1]));
            try
            {
                var buf = new byte[Math.Max(_device.GetMaxInputReportLength(), ControllerProtocol.ReportLength)];
                var end = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < end)
                {
                    int n;
                    try { n = _stream.Read(buf, 0, buf.Length); }
                    catch (TimeoutException) { continue; }
                    if (n < 17 || buf[4] != ControllerProtocol.CmdMotionData) continue;
                    var v = new short[6];
                    for (var i = 0; i < 6; i++) v[i] = BitConverter.ToInt16(buf, 5 + i * 2);
                    samples.Add(v);
                }
            }
            finally
            {
                _stream.Write(ControllerProtocol.BuildPacket(ControllerProtocol.CmdMotionStream, [0]));
            }
        }
        return samples;
    }

    public void SyncRgb()
    {
        lock (_lock) _stream.Write(ControllerProtocol.BuildPacket(ControllerProtocol.CmdSyncRgb, []));
    }

    public byte[] ReadProfile()
    {
        lock (_lock)
        {
            var image = new byte[ProfileLayout.ImageLength];
            for (var off = 0; off < image.Length; off += ControllerProtocol.ChunkSize)
            {
                var len = Math.Min(ControllerProtocol.ChunkSize, image.Length - off);
                var o = off;
                var resp = Transact(ControllerProtocol.BuildRead(off, len),
                    r => r[4] == ControllerProtocol.CmdReadProfileAck && ((r[6] << 8) | r[7]) == o && r[8] == len);
                resp.AsSpan(9, len).CopyTo(image.AsSpan(off));
            }
            return image;
        }
    }

    /// <summary>
    /// <paramref name="current"/>와 <paramref name="target"/>이 다른 바이트 구간만 RAM에 쓴다.
    /// 쓰기 후 다시 읽어 검증하고, 불일치면 예외.
    /// </summary>
    /// <returns>실제로 쓴 바이트 범위 (변경 없으면 null).</returns>
    public (int First, int Last)? WriteProfileRam(ReadOnlySpan<byte> current, ReadOnlySpan<byte> target)
    {
        if (current.Length != ProfileLayout.ImageLength || target.Length != ProfileLayout.ImageLength)
            throw new ArgumentException("프로필 길이 불일치");

        int first = -1, last = -1;
        for (var i = 0; i < target.Length; i++)
        {
            if (current[i] == target[i]) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return null;

        var targetCopy = target.ToArray();
        lock (_lock)
        {
            for (var off = first; off <= last; off += ControllerProtocol.ChunkSize)
            {
                var len = Math.Min(ControllerProtocol.ChunkSize, last - off + 1);
                var packet = ControllerProtocol.BuildWriteRam(off, targetCopy.AsSpan(off, len));
                // MSI 코드(DeviceMessage.IsDefaultAck)도 0x21의 ack를 "비동기 리포트가 아닌 아무 리포트"로 본다.
                // 그래서 쓰기 전에 쌓인 리포트를 비우고(= 쓰기 이후 도착한 것만), 다른 명령의 응답으로 알려진 것은 거른다.
                // 실제 반영 여부는 아래 재읽기 검증이 판단한다.
                var ack = Transact(packet, ControllerProtocol.IsPlausibleWriteAck, drainFirst: true);
                LastWriteAck = ack[4];
            }
        }

        var readBack = ReadProfile();
        if (!readBack.AsSpan(first, last - first + 1).SequenceEqual(targetCopy.AsSpan(first, last - first + 1)))
            throw new IOException("컨트롤러 프로필 쓰기 검증 실패");
        return (first, last);
    }

    /// <summary>
    /// 이미 도착해 OS 버퍼에 쌓여 있는 입력 리포트를 버린다 (이전 명령의 늦은 응답·비동기 리포트가
    /// 다음 명령의 ack로 오인되지 않게). 짧은 타임아웃으로 읽어 비어 있으면 바로 끝난다.
    /// </summary>
    private void Drain(byte[] buf)
    {
        var saved = _stream.ReadTimeout;
        _stream.ReadTimeout = 2;
        try
        {
            for (var i = 0; i < 64; i++) // HID 입력 버퍼 기본 32개
            {
                try { _ = _stream.Read(buf, 0, buf.Length); } // 내용은 버린다
                catch (TimeoutException) { return; }
            }
        }
        finally
        {
            _stream.ReadTimeout = saved;
        }
    }

    private byte[] Transact(byte[] packet, Func<byte[], bool> isAck, bool drainFirst = false)
    {
        var buf = new byte[Math.Max(_device.GetMaxInputReportLength(), ControllerProtocol.ReportLength)];
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (drainFirst) Drain(buf);
            _stream.Write(packet);
            var deadline = DateTime.UtcNow + AckTimeout;
            while (DateTime.UtcNow < deadline)
            {
                int n;
                try { n = _stream.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }
                // MSI 코드도 [0..3]은 검사하지 않는다 (입력 리포트 ID 미확인)
                if (n >= ControllerProtocol.ReportLength && isAck(buf))
                    return buf[..n];
            }
        }
        // MSI는 여기서 ResetDevice(0x28)를 보내지만 ClawHub는 절대 보내지 않는다.
        throw new TimeoutException($"컨트롤러 응답 없음 (cmd 0x{packet[4]:X2})");
    }

    public void Dispose() => _stream.Dispose();
}
