using System.IO.Pipelines;

namespace UsenetBackup.Core.Tests.Nntp;

/// <summary>
/// In-memory full-duplex stream pair (no sockets). Lets the NNTP client
/// and the fake server talk inside one process, so protocol tests run
/// anywhere — including sandboxes without loopback TCP.
/// </summary>
internal static class InMemoryTransport
{
    public static (Stream Client, Stream Server) Create()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        var server = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        return (client, server);
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;

        public DuplexStream(Stream read, Stream write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _write.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            _read.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) =>
            _write.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
