using System.Collections.Concurrent;
using System.IO;

using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace ICSharpCode.SharpZipLib.Zip.Compression
{
	/// <summary>
	/// Extension point for the DEFLATE decompressor used when reading zip entries.
	/// Lets callers substitute a pooled <see cref="Inflater"/> or a platform-native
	/// (System.IO.Compression) implementation for the default managed one.
	/// </summary>
	public interface IInflaterSource
	{
		/// <summary>
		/// Wraps <paramref name="compressedSource"/> (positioned at raw, headerless DEFLATE data)
		/// with a stream that yields the decompressed bytes.
		/// </summary>
		Stream CreateDecompressor(Stream compressedSource);
	}

	/// <summary>
	/// Default source: a fresh managed <see cref="Inflater"/> per call (the historical behaviour).
	/// </summary>
	public sealed class DefaultInflaterSource : IInflaterSource
	{
		/// <summary>Shared stateless instance.</summary>
		public static readonly IInflaterSource Default = new DefaultInflaterSource();

		/// <inheritdoc/>
		public Stream CreateDecompressor(Stream compressedSource)
			=> new InflaterInputStream(compressedSource, new Inflater(true));
	}

	/// <summary>
	/// Pools and reuses <see cref="Inflater"/> instances (each carries a 32 KiB window plus Huffman
	/// tables) to cut per-read allocation when reading many archives. Thread-safe.
	/// </summary>
	public sealed class PooledInflaterSource : IInflaterSource
	{
		/// <summary>Process-wide shared pool.</summary>
		public static readonly PooledInflaterSource Shared = new PooledInflaterSource();

		private readonly ConcurrentBag<Inflater> pool = new ConcurrentBag<Inflater>();

		/// <inheritdoc/>
		public Stream CreateDecompressor(Stream compressedSource)
		{
			if (pool.TryTake(out var inflater))
			{
				inflater.Reset();
			}
			else
			{
				inflater = new Inflater(true);
			}

			return new PooledStream(compressedSource, inflater, this);
		}

		private void Return(Inflater inflater) => pool.Add(inflater);

		// Returns the Inflater to the pool once the caller disposes the read stream.
		private sealed class PooledStream : InflaterInputStream
		{
			private PooledInflaterSource owner;
			private Inflater rented;

			public PooledStream(Stream baseInputStream, Inflater inflater, PooledInflaterSource owner)
				: base(baseInputStream, inflater)
			{
				this.owner = owner;
				rented = inflater;
			}

			protected override void Dispose(bool disposing)
			{
				base.Dispose(disposing);

				var o = owner;
				var r = rented;
				owner = null;
				rented = null;
				if (o != null && r != null)
				{
					o.Return(r);
				}
			}
		}
	}

	/// <summary>
	/// Uses the platform-native decompressor (<see cref="System.IO.Compression.DeflateStream"/>,
	/// i.e. zlib / zlib-ng), whose window lives in native memory (off the GC heap) and is typically
	/// faster on modern runtimes.
	/// </summary>
	public sealed class SystemInflaterSource : IInflaterSource
	{
		/// <summary>Shared stateless instance.</summary>
		public static readonly IInflaterSource Default = new SystemInflaterSource();

		/// <inheritdoc/>
		public Stream CreateDecompressor(Stream compressedSource)
			=> new System.IO.Compression.DeflateStream(compressedSource,
				System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
	}
}
