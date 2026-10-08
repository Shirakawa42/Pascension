using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Shards.Preflight
{
    // The pointer lease is owned for exactly the accessor lifetime. A parent-side
    // command/response barrier prevents concurrent overwrite/read of the body.
    internal sealed unsafe class MappedBody : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly int _length;
        private byte* _pointer;
        internal MappedBody(string path, int length)
        {
            if (new FileInfo(path).Length != length) throw new InvalidOperationException("Shared body length mismatch");
            _length = length;
            _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, length, MemoryMappedFileAccess.ReadWrite);
            _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointer = pointer + _view.PointerOffset;
        }
        internal void Write(byte[] source, bool spanCopy)
        {
            if (_pointer == null) throw new ObjectDisposedException(nameof(MappedBody));
            if (source.Length != _length) throw new InvalidOperationException("Shared body copy bounds mismatch");
            if (spanCopy) source.AsSpan().CopyTo(new Span<byte>(_pointer, _length));
            else _view.WriteArray(0, source, 0, source.Length);
        }
        public void Dispose()
        {
            if (_pointer == null) return;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
            _view.Dispose();
            _file.Dispose();
        }
    }
}
