using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Data.Json;

namespace ClawWidget.Ipc
{
    /// <summary>
    /// \\.\pipe\ClawHub.Service 클라이언트 (줄 단위 UTF-8 JSON).
    ///
    /// AppContainer에서는 NamedPipeClientStream이 실패하므로 CreateFile2(실패 시 CreateFileFromApp)로 직접 연다.
    /// 서비스 쪽 파이프에는 ALL APPLICATION PACKAGES ReadWrite + Low 무결성 레이블이 있어야 하고,
    /// 서비스는 패키지 패밀리 이름이 "ClawHub.Widget_"로 시작하는 AppContainer만 받는다.
    ///
    /// 동기 핸들 하나에 동시에 ReadFile/WriteFile을 걸면 I/O 관리자가 직렬화해 버리므로
    /// (대기 중인 읽기가 쓰기를 막음) 백그라운드 읽기 루프 없이 "요청 한 줄 쓰기 → 같은 Id 응답 줄 읽기"만 한다.
    /// 위젯은 Subscribe하지 않으므로 서버가 먼저 보내는 푸시(Id 0)는 원칙적으로 없고, 와도 건너뛴다.
    /// </summary>
    public sealed class PipeClient : IDisposable
    {
        public const string PipePath = @"\\.\pipe\ClawHub.Service";
        private const int MaxLineBytes = 1024 * 1024;

        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private Connection _conn;
        private int _nextId;
        private bool _disposed;

        public bool IsConnected => _conn != null;

        /// <summary>마지막 연결 실패 사유 (Win32 오류 코드 기반, UI 보조 문구용).</summary>
        public string LastError { get; private set; }

        /// <summary>요청을 보내고 응답을 기다린다. 연결 불가·시간 초과·끊김이면 null (연결은 정리되고 다음 호출에서 재접속).</summary>
        public async Task<Response> SendAsync(JsonObject request, int timeoutMs = 5000)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return null;
                if (_conn == null && !TryConnect()) return null;

                var conn = _conn;
                var id = ++_nextId;
                if (id == int.MaxValue) _nextId = 0;
                request["Id"] = JsonValue.CreateNumberValue(id);
                var line = request.Stringify();

                // 시간 초과 후 Abort된 핸들에서 던지는 예외까지 여기서 삼킨다 (관찰되지 않은 Task 예외 방지)
                var work = Task.Run(() =>
                {
                    try { return conn.Exchange(line, id); }
                    catch (Exception) { return (Response)null; }
                });
                var finished = await Task.WhenAny(work, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != work)
                {
                    LastError = "응답 시간 초과";
                    Drop();
                    return null;
                }
                var resp = await work.ConfigureAwait(false);
                if (resp == null)
                {
                    LastError = "연결이 끊겼어요";
                    Drop();
                }
                return resp;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Drop();
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>연결을 끊는다 (일시 중단 등). 다음 SendAsync에서 다시 연결한다.</summary>
        public void Disconnect()
        {
            Drop();
        }

        private bool TryConnect()
        {
            var h = Native.CreateFile2(PipePath, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, Native.OPEN_EXISTING, IntPtr.Zero);
            var err = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            if (h.IsInvalid && err != Native.ERROR_FILE_NOT_FOUND && err != Native.ERROR_PIPE_BUSY)
            {
                // CreateFile2가 막히는 환경 대비 (브로커 경유 경로)
                h.Dispose();
                h = Native.CreateFileFromApp(PipePath, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) err = Marshal.GetLastWin32Error();
            }
            if (h.IsInvalid)
            {
                h.Dispose();
                LastError = Describe(err);
                return false;
            }
            _conn = new Connection(h);
            LastError = null;
            return true;
        }

        private static string Describe(int err)
        {
            switch (err)
            {
                case Native.ERROR_FILE_NOT_FOUND: return "서비스가 실행 중이 아니에요";
                case Native.ERROR_ACCESS_DENIED: return "서비스가 위젯 접속을 허용하지 않아요 (오류 5)";
                case Native.ERROR_PIPE_BUSY: return "서비스가 바빠요, 곧 다시 시도해요";
                default: return "연결 오류 " + err;
            }
        }

        private void Drop()
        {
            var c = _conn;
            _conn = null;
            c?.Abort();
        }

        public void Dispose()
        {
            _disposed = true;
            Drop();
        }

        /// <summary>파이프 핸들 하나 + 받은 바이트 버퍼. Exchange는 한 번에 하나만 (PipeClient._lock).</summary>
        private sealed class Connection
        {
            private readonly SafeFileHandle _handle;
            private byte[] _buf = new byte[8192];
            private int _len;

            public Connection(SafeFileHandle handle)
            {
                _handle = handle;
            }

            /// <summary>요청 한 줄을 쓰고 같은 Id의 응답이 올 때까지 줄을 읽는다. 끊기면 null.</summary>
            public Response Exchange(string line, int id)
            {
                if (!WriteLine(line)) return null;
                while (true)
                {
                    var text = ReadLine();
                    if (text == null) return null;
                    if (text.Length == 0) continue;
                    var resp = Response.Parse(text);
                    if (resp == null) continue;
                    if (resp.Id == id) return resp;
                    // Id 0 = 푸시 이벤트, 다른 Id = 시간 초과로 버린 이전 요청의 늦은 응답 → 무시
                }
            }

            private bool WriteLine(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                var offset = 0;
                while (offset < bytes.Length)
                {
                    int written;
                    var chunk = new byte[bytes.Length - offset];
                    Buffer.BlockCopy(bytes, offset, chunk, 0, chunk.Length);
                    if (!Native.WriteFile(_handle, chunk, chunk.Length, out written, IntPtr.Zero) || written <= 0) return false;
                    offset += written;
                }
                return true;
            }

            private string ReadLine()
            {
                var scanFrom = 0;
                while (true)
                {
                    var nl = Array.IndexOf(_buf, (byte)'\n', scanFrom, _len - scanFrom);
                    if (nl >= 0)
                    {
                        var count = nl;
                        if (count > 0 && _buf[count - 1] == (byte)'\r') count--;
                        var s = Encoding.UTF8.GetString(_buf, 0, count);
                        var rest = _len - (nl + 1);
                        Buffer.BlockCopy(_buf, nl + 1, _buf, 0, rest);
                        _len = rest;
                        // UTF-8 BOM이 붙어 올 경우 제거
                        return s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;
                    }
                    scanFrom = _len;

                    if (_len == _buf.Length)
                    {
                        if (_buf.Length >= MaxLineBytes) return null; // 비정상적으로 긴 줄 → 연결 정리
                        Array.Resize(ref _buf, _buf.Length * 2);
                    }

                    var tmp = new byte[_buf.Length - _len];
                    int read;
                    if (!Native.ReadFile(_handle, tmp, tmp.Length, out read, IntPtr.Zero))
                    {
                        // ERROR_MORE_DATA는 메시지 모드에서만 — 바이트 모드 파이프라 그 외는 전부 끊김
                        if (Marshal.GetLastWin32Error() != Native.ERROR_MORE_DATA) return null;
                    }
                    if (read <= 0) return null;
                    Buffer.BlockCopy(tmp, 0, _buf, _len, read);
                    _len += read;
                }
            }

            /// <summary>
            /// 다른 스레드에서 막혀 있는 ReadFile/WriteFile을 취소하고 핸들을 닫는다.
            /// SafeHandle은 P/Invoke 사용 중엔 실제로 닫히지 않으므로 먼저 CancelIoEx로 깨운다.
            /// </summary>
            public void Abort()
            {
                try
                {
                    if (!_handle.IsClosed && !_handle.IsInvalid) Native.CancelIoEx(_handle, IntPtr.Zero);
                }
                catch (Exception)
                {
                    // 취소 실패는 무시 — Dispose가 끝내 정리한다
                }
                _handle.Dispose();
            }
        }

        private static class Native
        {
            public const uint GENERIC_READ = 0x80000000;
            public const uint GENERIC_WRITE = 0x40000000;
            public const uint OPEN_EXISTING = 3;
            public const int ERROR_FILE_NOT_FOUND = 2;
            public const int ERROR_ACCESS_DENIED = 5;
            public const int ERROR_MORE_DATA = 234;
            public const int ERROR_PIPE_BUSY = 231;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFile2(
                string lpFileName, uint dwDesiredAccess, uint dwShareMode, uint dwCreationDisposition, IntPtr pCreateExParams);

            [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", EntryPoint = "CreateFileFromAppW", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFileFromApp(
                string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
                uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);
        }
    }
}
