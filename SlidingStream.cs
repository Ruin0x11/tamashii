using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tamashii
{
    public class SlidingStream : Stream
    {
        private readonly object _writeLock = new object();
        private readonly object _readLock = new object();
        private readonly ConcurrentQueue<byte[]> _pendingSegments = new ConcurrentQueue<byte[]>();
        private byte[]? _extraSegment = null;
        private int _pos;

        private readonly SemaphoreSlim _smSegmentsAvailable = new SemaphoreSlim(0);

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeLock)
            {
                var copy = new byte[count];
                Array.Copy(buffer, offset, copy, 0, count);

                _pendingSegments.Enqueue(copy);
                _smSegmentsAvailable.Release(1);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int bytesToRead)
        {
            lock (_readLock)
            {
                var bytesRead = 0;

                while (bytesToRead > 0)
                {
                    byte[]? segment = null;

                    if (_extraSegment != null)
                    {
                        segment = _extraSegment;
                        _extraSegment = null;
                    }
                    else
                    {
                        if (_smSegmentsAvailable.CurrentCount == 0 && bytesRead > 0)
                        {
                            return bytesRead;
                        }

                        // _smSegmentsAvailable.Wait(_cancel);
                        _smSegmentsAvailable.Wait();

                        if (!_pendingSegments.TryDequeue(out segment))
                        {
                            throw new InvalidOperationException("No segment found, despite semaphore");
                        }
                    }

                    var copyCount = Math.Min(bytesToRead, segment.Length);
                    Array.Copy(segment, 0, buffer, offset + bytesRead, copyCount);
                    bytesToRead -= copyCount;
                    bytesRead += copyCount;

                    var extraCount = segment.Length - copyCount;
                    if (extraCount > 0)
                    {
                        _extraSegment = new byte[extraCount];
                        Array.Copy(segment, copyCount, _extraSegment, 0, extraCount);
                    }
                }

                _pos += bytesRead;
                return bytesRead;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            //could be extended here with a proper async read
            var result = Read(buffer, offset, count);
            return Task.FromResult(result);
        }

        protected override void Dispose(bool disposing)
        {
            _smSegmentsAvailable.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Flush() { }

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }
    }


}
