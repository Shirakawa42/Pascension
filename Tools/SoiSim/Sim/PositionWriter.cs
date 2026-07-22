using System;
using System.IO;
using Shards.Bots;

namespace SoiSim
{
    /// <summary>SOIP binary position files — the contract between C# self-play and
    /// numpy/PyTorch. Header carries the encoder schema; loaders refuse mismatches.
    /// Record layout (little-endian, 3096 bytes):
    ///   f32 x[768] · f32 z · f32 q · u64 gameSeed · u16 moveIndex · u8 viewerSeat ·
    ///   u8 flags (bit0 = bootstrap/no-search-q) · 4B pad.
    /// numpy: np.fromfile(f, dtype=[('x','&lt;f4',768),('z','&lt;f4'),('q','&lt;f4'),
    ///   ('seed','&lt;u8'),('move','&lt;u2'),('seat','u1'),('flags','u1'),('pad','V4')],
    ///   offset=32)</summary>
    public sealed class PositionWriter : IDisposable
    {
        public const uint Magic = 0x50494F53; // "SOIP" little-endian
        public const ushort FormatVersion = 1;
        public static readonly int RecordSize = ShardsStateEncoder.FeatureCount * 4 + 4 + 4 + 8 + 2 + 1 + 1 + 4;

        private readonly BinaryWriter _writer;
        private readonly int _featureCount;
        public int Written { get; private set; }

        /// <summary>Writes the CURRENT encoder schema. The schema overload writes a
        /// specific one — v1 records train nets for ranks that deploy the frozen
        /// pooled encoding (the probed-and-adopted config).</summary>
        public PositionWriter(string path)
            : this(path, ShardsStateEncoder.SchemaVersion, ShardsStateEncoder.FeatureCount) { }

        public PositionWriter(string path, int schemaVersion, int featureCount)
        {
            _featureCount = featureCount;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write));
            _writer.Write(Magic);
            _writer.Write(FormatVersion);
            _writer.Write((ushort)schemaVersion);
            _writer.Write((uint)featureCount);
            _writer.Write((uint)(featureCount * 4 + 4 + 4 + 8 + 2 + 1 + 1 + 4));
            _writer.Write(new byte[16]); // reserved → 32-byte header
        }

        public void Write(float[] features, float z, float q, ulong gameSeed,
            ushort moveIndex, byte viewerSeat, byte flags)
        {
            for (int i = 0; i < _featureCount; i++)
                _writer.Write(features[i]);
            _writer.Write(z);
            _writer.Write(q);
            _writer.Write(gameSeed);
            _writer.Write(moveIndex);
            _writer.Write(viewerSeat);
            _writer.Write(flags);
            _writer.Write(0); // pad
            Written++;
        }

        public void Dispose() => _writer.Dispose();
    }
}
