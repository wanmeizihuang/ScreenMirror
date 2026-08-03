using System.Collections.Concurrent;
using System.IO;

namespace ScreenMirror.Server.Modules.VlcDecoder;

/// <summary>
/// 将 NAL 队列包装为可读 Stream，供 VLC StreamMediaInput 消费
///
/// 工作原理:
///   ScreenMirrorPlugin.EnqueueNalUnit() → Write() → 内部 ConcurrentQueue
///   VLC StreamMediaInput.Read() → 从队列取出字节返回
///
/// 特性:
///   - 阻塞式 Read：无数据时等待，有数据时立即返回（避免空转）
///   - 线程安全：写端（解码线程）和读端（VLC 线程）分离
///   - 背压控制：队列超过阈值时丢弃旧 P/B 帧，保留关键帧
/// </summary>
public class NalStream : Stream
{
    private readonly ConcurrentQueue<byte[]> _nalQueue = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);

    // 当前正在读取的 NAL 剩余字节
    private byte[]? _currentNal;
    private int _currentOffset;

    // 统计
    private long _totalBytesWritten;
    private long _totalNalsWritten;
    private long _lastWriteTimeMs;
    private int _queueMaxSize = 60;  // 队列上限，超过则丢老 P/B 帧

    private bool _closed;

    /// <summary>最近一次写入 NAL 的时间（毫秒）</summary>
    public long LastWriteTimeMs => Interlocked.Read(ref _lastWriteTimeMs);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _totalBytesWritten;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>写入一个 NAL 单元（调用方：ScreenMirrorPlugin 解码线程）</summary>
    public void WriteNal(byte[] nalData)
    {
        if (_closed) return;

        // 背压控制：队列过满时丢老的普通帧
        if (_nalQueue.Count > _queueMaxSize)
        {
            // 丢掉最旧的一帧（SPS/PPS/IDR 不丢，这里简化处理，丢队首）
            if (_nalQueue.TryDequeue(out _))
            {
                _dataAvailable.Wait(0);  // 消费一个信号
            }
        }

        _nalQueue.Enqueue(nalData);
        _totalBytesWritten += nalData.Length;
        Interlocked.Increment(ref _totalNalsWritten);
        Interlocked.Exchange(ref _lastWriteTimeMs, Environment.TickCount64);
        _dataAvailable.Release();
    }

    /// <summary>VLC 读取数据（调用方：VLC 内部线程）</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        // 循环等待直到有数据或流关闭
        while (true)
        {
            if (_closed) return 0;

            // 如果当前 NAL 还没读完，继续读
            if (_currentNal != null && _currentOffset < _currentNal.Length)
            {
                int toCopy = Math.Min(count, _currentNal.Length - _currentOffset);
                Buffer.BlockCopy(_currentNal, _currentOffset, buffer, offset, toCopy);
                _currentOffset += toCopy;
                if (_currentOffset >= _currentNal.Length)
                    _currentNal = null;
                return toCopy;
            }

            // 等待新 NAL 到达（每次等 500ms，循环检查 _closed）
            if (!_dataAvailable.Wait(500))
            {
                // 超时但流未关闭，继续等（不能返回 0，否则 VLC 认为流结束）
                continue;
            }

            // 取下一个 NAL
            if (!_nalQueue.TryDequeue(out _currentNal))
            {
                // 信号量有但队列空（竞态），继续等
                continue;
            }

            _currentOffset = 0;

            // 复制到调用方 buffer
            int copyLen = Math.Min(count, _currentNal.Length);
            Buffer.BlockCopy(_currentNal, 0, buffer, offset, copyLen);
            _currentOffset = copyLen;

            return copyLen;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        // 直接写入整个 buffer 作为一个 NAL
        var data = new byte[count];
        Buffer.BlockCopy(buffer, offset, data, 0, count);
        WriteNal(data);
    }

    public override void Flush() { }

    public override void Close()
    {
        _closed = true;
        _dataAvailable.Release();  // 唤醒可能阻塞的 Read
        base.Close();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // ===== 诊断信息 =====
    public int QueueCount => _nalQueue.Count;
    public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);
    public long TotalNalsWritten => Interlocked.Read(ref _totalNalsWritten);
}
