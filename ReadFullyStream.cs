using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tamashii
{
    public class ReadFullyStream : Stream
    {
        private readonly Stream sourceStream;
        private long size;
        private long pos; // psuedo-position
        private readonly byte[] readAheadBuffer;
        private int readAheadLength;
        private int readAheadOffset;

        public ReadFullyStream(Stream sourceStream, long size)
        {
            this.sourceStream = sourceStream;
            this.size = size;
            readAheadBuffer = new byte[4096];
        }
        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override void Flush()
        {
            throw new InvalidOperationException();
        }

        public override long Length
        {
            get { return size; }
        }

        public override long Position
        {
            get
            {
                return pos;
            }
            set
            {
                throw new InvalidOperationException();
            }
        }


        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = 0;
            while (bytesRead < count)
            {
                int readAheadAvailableBytes = readAheadLength - readAheadOffset;
                int bytesRequired = count - bytesRead;
                if (readAheadAvailableBytes > 0)
                {
                    int toCopy = Math.Min(readAheadAvailableBytes, bytesRequired);
                    Array.Copy(readAheadBuffer, readAheadOffset, buffer, offset + bytesRead, toCopy);
                    bytesRead += toCopy;
                    readAheadOffset += toCopy;
                }
                else
                {
                    readAheadOffset = 0;
                    readAheadLength = sourceStream.Read(readAheadBuffer, 0, readAheadBuffer.Length);
                    if (readAheadLength == 0)
                    {
                        if (pos == size)
                            break;
                    }
                    else
                    {
                        // Debug.WriteLine(string.Format("Read {0} bytes (requested {1}, wanted {2})", readAheadLength, readAheadBuffer.Length, count));
                    }
                }
            }
            pos += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new InvalidOperationException();
        }

        public override void SetLength(long value)
        {
            throw new InvalidOperationException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException();
        }
    }
}
