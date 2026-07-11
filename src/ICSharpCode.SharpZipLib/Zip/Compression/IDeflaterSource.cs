using System;
using System.Collections.Concurrent;
using System.IO;

using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace ICSharpCode.SharpZipLib.Zip.Compression
{
	/// <summary>
	/// Extension point for the DEFLATE compressor used when writing zip entries.
	/// Symmetric to <see cref="IInflaterSource"/>: lets callers substitute a pooled
	/// <see cref="Deflater"/> or a platform-native (System.IO.Compression) implementation.
	/// </summary>
	public interface IDeflaterSource
	{
		/// <summary>
		/// Wraps <paramref name="destination"/> with a stream that raw-DEFLATE-compresses
		/// (headerless) everything written to it. <paramref name="level"/> is the SharpZipLib
		/// scale (0..9). When <paramref name="leaveOpen"/> is false the returned stream closes
		/// <paramref name="destination"/> on dispose.
		/// </summary>
		Stream CreateCompressor(Stream destination, int level, bool leaveOpen);
	}

	/// <summary>Default source: a fresh managed <see cref="Deflater"/> per call (historical behaviour).</summary>
	public sealed class DefaultDeflaterSource : IDeflaterSource
	{
		/// <summary>Shared stateless instance.</summary>
		public static readonly IDeflaterSource Default = new DefaultDeflaterSource();

		/// <inheritdoc/>
		public Stream CreateCompressor(Stream destination, int level, bool leaveOpen)
			=> new DeflaterOutputStream(destination, new Deflater(level, true)) { IsStreamOwner = !leaveOpen };
	}

	/// <summary>
	/// Thread-safe pool of raw (no zlib header/footer) <see cref="Deflater"/> instances for zip
	/// writing. Each Deflater carries a large window plus hash tables, so reuse cuts per-write
	/// allocation sharply. Shared by <see cref="PooledDeflaterSource"/> (which wraps a rented deflater
	/// in a return-on-dispose stream) and <see cref="PooledDeflaterFactory"/> (which hands one to
	/// ZipOutputStream directly), so the rent-or-create policy lives in exactly one place.
	/// </summary>
	internal sealed class DeflaterPool
	{
		private readonly ConcurrentBag<Deflater> pool = new ConcurrentBag<Deflater>();

		/// <summary>
		/// Rents a Deflater configured for <paramref name="level"/> — reset and re-levelled if reused,
		/// otherwise freshly created raw (zip entries carry no zlib header/footer).
		/// </summary>
		public Deflater Rent(int level)
		{
			if (pool.TryTake(out var deflater))
			{
				deflater.Reset();
				deflater.SetLevel(level);
				return deflater;
			}

			return new Deflater(level, noZlibHeaderOrFooter: true);
		}

		/// <summary>Returns a finished Deflater for reuse.</summary>
		public void Return(Deflater deflater)
		{
			if (deflater != null)
			{
				pool.Add(deflater);
			}
		}
	}

	/// <summary>
	/// Pools and reuses <see cref="Deflater"/> instances (each carries a large window plus hash
	/// tables) to cut per-write allocation when producing many entries/archives. Thread-safe.
	/// </summary>
	public sealed class PooledDeflaterSource : IDeflaterSource
	{
		/// <summary>Process-wide shared pool.</summary>
		public static readonly PooledDeflaterSource Shared = new PooledDeflaterSource();

		private readonly DeflaterPool pool = new DeflaterPool();

		/// <inheritdoc/>
		public Stream CreateCompressor(Stream destination, int level, bool leaveOpen)
		{
			var deflater = pool.Rent(level);
			return new PooledStream(destination, deflater, this) { IsStreamOwner = !leaveOpen };
		}

		private void Return(Deflater deflater) => pool.Return(deflater);

		// Returns the Deflater to the pool once the caller disposes (and thus finishes) the stream.
		private sealed class PooledStream : DeflaterOutputStream
		{
			private PooledDeflaterSource owner;
			private Deflater rented;

			public PooledStream(Stream baseOutputStream, Deflater deflater, PooledDeflaterSource owner)
				: base(baseOutputStream, deflater)
			{
				this.owner = owner;
				rented = deflater;
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
	/// Uses the platform-native compressor (<see cref="System.IO.Compression.DeflateStream"/>,
	/// i.e. zlib / zlib-ng). Faster on modern runtimes; note that on .NET Framework the BCL caps
	/// the level at <c>Optimal</c> (no level-9 equivalent), so the ratio is slightly below the
	/// managed Deflater at level 9 there — hence this being an opt-in policy choice.
	/// </summary>
	public sealed class SystemDeflaterSource : IDeflaterSource
	{
		/// <summary>Shared stateless instance.</summary>
		public static readonly IDeflaterSource Default = new SystemDeflaterSource();

		/// <inheritdoc/>
		public Stream CreateCompressor(Stream destination, int level, bool leaveOpen)
			=> new System.IO.Compression.DeflateStream(destination, MapLevel(level), leaveOpen);

		private static System.IO.Compression.CompressionLevel MapLevel(int level)
		{
			if (level <= 0)
			{
				return System.IO.Compression.CompressionLevel.NoCompression;
			}

			if (level <= 2)
			{
				return System.IO.Compression.CompressionLevel.Fastest;
			}

#if NET6_0_OR_GREATER
			// SmallestSize maps to zlib level 9; only available on .NET 6+. On netstandard/netfx
			// the highest available is Optimal (~level 6-7), which is the documented ratio gap.
			if (level >= 7)
			{
				return System.IO.Compression.CompressionLevel.SmallestSize;
			}
#endif

			return System.IO.Compression.CompressionLevel.Optimal;
		}
	}

	/// <summary>
	/// Supplies the <see cref="Deflater"/> instance used by <see cref="ICSharpCode.SharpZipLib.Zip.ZipOutputStream"/>
	/// (which, being a DeflaterOutputStream, needs a Deflater rather than a wrapping stream). Lets callers pool
	/// Deflaters across many streams — each carries large window/hash buffers, so pooling cuts allocation sharply.
	/// </summary>
	public interface IDeflaterFactory
	{
		/// <summary>Rents a Deflater configured for the given level (raw / no zlib header for zip).</summary>
		Deflater Rent(int level, bool noZlibHeaderOrFooter);

		/// <summary>Returns a finished Deflater for reuse (no-op for the default factory).</summary>
		void Return(Deflater deflater);
	}

	/// <summary>Default factory: a fresh <see cref="Deflater"/> per stream (historical behaviour).</summary>
	public sealed class DefaultDeflaterFactory : IDeflaterFactory
	{
		/// <summary>Shared stateless instance.</summary>
		public static readonly IDeflaterFactory Instance = new DefaultDeflaterFactory();

		/// <inheritdoc/>
		public Deflater Rent(int level, bool noZlibHeaderOrFooter) => new Deflater(level, noZlibHeaderOrFooter);

		/// <inheritdoc/>
		public void Return(Deflater deflater) { }
	}

	/// <summary>
	/// Pools raw (no-zlib-header) <see cref="Deflater"/> instances for zip writing. Thread-safe.
	/// Assumes the zip case (noZlibHeaderOrFooter = true); the flag is honoured only when creating a new one.
	/// </summary>
	public sealed class PooledDeflaterFactory : IDeflaterFactory
	{
		/// <summary>Process-wide shared pool.</summary>
		public static readonly PooledDeflaterFactory Shared = new PooledDeflaterFactory();

		private readonly DeflaterPool pool = new DeflaterPool();

		/// <inheritdoc/>
		public Deflater Rent(int level, bool noZlibHeaderOrFooter)
		{
			// The pool only ever holds raw (no zlib header) deflaters (the zip case). Reset() cannot change
			// a pooled instance's header mode, so a zlib-framed request could not be honoured — reject it.
			if (!noZlibHeaderOrFooter)
			{
				throw new ArgumentException(
					"PooledDeflaterFactory only pools raw (no zlib header/footer) deflaters.", nameof(noZlibHeaderOrFooter));
			}

			return pool.Rent(level);
		}

		/// <inheritdoc/>
		public void Return(Deflater deflater) => pool.Return(deflater);
	}
}
