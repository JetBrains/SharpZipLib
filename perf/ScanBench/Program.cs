using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace ScanBench
{
	// Harness for the archive-open hot path (fast-scan-many-opens of nupkgs/OPCs).
	//
	//   dotnet run -c Release -f net8.0 -- --scenario open --inmem --reps 15
	//   dotnet run -c Release -f net48  -- --scenario nuspec --arm system --inmem
	//   dotnet run -c Release -f net9.0 -- --scenario compress --arm system
	//
	// --inmem preloads package bytes and opens each from a MemoryStream, so the measured loop has
	// NO disk I/O -> the timing reflects parsing CPU, which is what the opts move. Without it the
	// loop opens files from disk (realistic, but noisy). Reps run the whole set R times; we report
	// min/median/mean/stddev of per-open time (min = cleanest CPU estimate). Allocation is
	// deterministic, measured once. Single-threaded, High process priority to cut scheduler noise.
	internal static class Program
	{
		private static string s_arm = "sharpzip";        // sharpzip | pooled | system
		[ThreadStatic] private static byte[] s_readBuf;   // reused so the read buffer is not counted per-open
		private static long s_bytes;                      // total bytes actually (de)compressed (correctness check)
		private static int s_fails;                       // ops that threw (must be 0 for a valid run)
		private static byte[] s_payload;                  // compress scenario: representative input
		private static long s_compressedLen;              // compress scenario: last output size (ratio)
		private static byte[] s_smallPayload;             // zoswrite scenario: one small entry body

		private static int Main(string[] args)
		{
			string dir = GetOpt(args, "--dir", null);
			int synth = int.Parse(GetOpt(args, "--synth", "0"));
			int reps = int.Parse(GetOpt(args, "--reps", "12"));
			string scenario = GetOpt(args, "--scenario", "open");
			s_arm = GetOpt(args, "--arm", "sharpzip");
			bool inmem = Array.IndexOf(args, "--inmem") >= 0;
			int budgetMB = int.Parse(GetOpt(args, "--budgetMB", "1500"));
			int maxFileMB = int.Parse(GetOpt(args, "--maxFileMB", "64"));

			try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }

			// A unit of work = open one archive from a fresh stream (disk or in-memory), or (compress) one payload.
			List<Func<Stream>> work;
			Action<Func<Stream>> op;

			if (scenario == "compress")
			{
				s_payload = BuildPayload();
				work = Enumerable.Range(0, 25).Select(_ => (Func<Stream>)(() => null)).ToList();
				op = _ => CompressPayload();
				Console.WriteLine($"compress payload: {s_payload.Length / 1024.0 / 1024.0:N2} MB representative text");
			}
			else if (scenario == "zoswrite")
			{
				s_smallPayload = new byte[1024];
				new Random(7).NextBytes(s_smallPayload);
				work = Enumerable.Range(0, 200).Select(_ => (Func<Stream>)(() => null)).ToList();
				op = _ => ZosWrite();
				Console.WriteLine("zoswrite: 200 ZipOutputStreams/rep, one small entry each");
			}
			else
			{
				string[] paths = ResolvePaths(ref dir, ref synth);
				if (paths.Length == 0) { Console.Error.WriteLine("no packages"); return 2; }

				if (inmem)
				{
					var blobs = new List<byte[]>();
					long budget = (long)budgetMB * 1024 * 1024, maxFile = (long)maxFileMB * 1024 * 1024, total = 0;
					foreach (var p in paths)
					{
						var fi = new FileInfo(p);
						if (fi.Length > maxFile || total + fi.Length > budget) continue;
						blobs.Add(File.ReadAllBytes(p));
						total += fi.Length;
					}
					work = blobs.Select(b => (Func<Stream>)(() => new MemoryStream(b, writable: false))).ToList();
					Console.WriteLine($"corpus: {work.Count}/{paths.Length} packages preloaded in memory " +
						$"({total / 1024.0 / 1024.0:N0} MB; skipped >{maxFileMB} MB or over budget)");
				}
				else
				{
					work = paths.Select(p => (Func<Stream>)(() => File.OpenRead(p))).ToList();
					Console.WriteLine($"corpus: {work.Count} packages from disk under {dir}");
				}
				op = scenario == "nuspec" ? (Action<Func<Stream>>)ReadNuspec : ScanOpen;
			}

			Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}  |  scenario={scenario} " +
				$"arm={s_arm} reps={reps} inmem={inmem}  ops/rep={work.Count}");

			// Warmup: JIT + (disk mode) warm OS cache.
			foreach (var w in work) Safe(op, w);
			foreach (var w in work) Safe(op, w);

			// Allocation: deterministic, measure one clean rep.
			GcQuiesce();
			long a0 = GC.GetAllocatedBytesForCurrentThread();
			s_bytes = 0; s_fails = 0;
			foreach (var w in work) Safe(op, w);
			long allocPerRep = GC.GetAllocatedBytesForCurrentThread() - a0;

			// Timing: R reps, min/median/mean/stddev of per-op microseconds.
			var perOpUs = new double[reps];
			for (int r = 0; r < reps; r++)
			{
				GcQuiesce();
				// Suppress GC during the rep so pauses don't pollute the CPU timing (budget > per-rep alloc).
				bool noGc = false;
				try { noGc = GC.TryStartNoGCRegion(96L * 1024 * 1024); } catch { }
				var sw = Stopwatch.StartNew();
				foreach (var w in work) Safe(op, w);
				sw.Stop();
				if (noGc) { try { GC.EndNoGCRegion(); } catch { } }
				perOpUs[r] = sw.Elapsed.TotalMilliseconds * 1000.0 / work.Count;
			}

			Array.Sort(perOpUs);
			double min = perOpUs[0];
			double median = perOpUs[reps / 2];
			double mean = perOpUs.Average();
			double sd = Math.Sqrt(perOpUs.Select(x => (x - mean) * (x - mean)).Average());
			double allocKb = allocPerRep / 1024.0 / work.Count;

			Console.WriteLine($"RESULT  per-op: min={min:N1} med={median:N1} mean={mean:N1} sd={sd:N1} us" +
				$"  |  alloc/op={allocKb:N1} KB  |  fails={s_fails} decompressed={s_bytes / 1024.0 / 1024.0:N1} MB");
			if (scenario == "compress")
				Console.WriteLine($"COMPRESS  payload={s_payload.Length / 1024.0 / 1024.0:N2} MB  " +
					$"compressed={s_compressedLen / 1024.0:N0} KB  ratio={(double)s_compressedLen / s_payload.Length:P2}");
			return 0;
		}

		// "open": scan the central directory only, touch each entry name. No decompression.
		private static void ScanOpen(Func<Stream> openStream)
		{
			using (var zf = new ZipFile(openStream()))
				foreach (ZipEntry e in zf) { var _ = e.Name; }
		}

		// "nuspec": open + locate the root .nuspec + fully read it (one inflate) via the selected arm.
		private static void ReadNuspec(Func<Stream> openStream)
		{
			using (var zf = new ZipFile(openStream()))
			{
				switch (s_arm)
				{
					case "pooled": zf.InflaterSource = PooledInflaterSource.Shared; break;
					case "system": zf.InflaterSource = SystemInflaterSource.Default; break;
				}
				foreach (ZipEntry e in zf)
				{
					if (e.IsFile && e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("/"))
					{
						var buf = s_readBuf ?? (s_readBuf = new byte[16 * 1024]);
						using (var s = zf.GetInputStream(e))
						{
							int n;
							while ((n = s.Read(buf, 0, buf.Length)) > 0) { s_bytes += n; }
						}
						break;
					}
				}
			}
		}

		// "compress": compress a fixed representative payload via the selected deflater arm.
		private static void CompressPayload()
		{
			IDeflaterSource src =
				s_arm == "pooled" ? PooledDeflaterSource.Shared :
				s_arm == "system" ? SystemDeflaterSource.Default :
				DefaultDeflaterSource.Default;

			var counter = new CountingStream();
			using (var cs = src.CreateCompressor(counter, 9, leaveOpen: true))
				cs.Write(s_payload, 0, s_payload.Length);
			s_compressedLen = counter.Count;
			s_bytes += s_payload.Length;
		}

		private static string[] ResolvePaths(ref string dir, ref int synth)
		{
			if (dir == null && synth == 0)
			{
				string guess = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\JetBrains\~\NugetLocalRestore");
				if (Directory.Exists(guess)) dir = guess; else synth = 200;
			}
			if (dir != null) return Directory.GetFiles(dir, "*.nupkg", SearchOption.AllDirectories);
			string tmp = Path.Combine(Path.GetTempPath(), "ScanBench_synth");
			return SynthesizeCorpus(tmp, synth);
		}

		private static void Safe(Action<Func<Stream>> op, Func<Stream> w)
		{
			try { op(w); } catch { s_fails++; }
		}

		private static void GcQuiesce()
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}

		// "zoswrite": create a ZipOutputStream (default vs pooled Deflater factory) and write one small entry.
		// Isolates the per-stream Deflater allocation, which pooling eliminates.
		private static void ZosWrite()
		{
			var dest = new CountingStream();
			ZipOutputStream zos = s_arm == "pooled"
				? new ZipOutputStream(dest, PooledDeflaterFactory.Shared)
				: new ZipOutputStream(dest);
			using (zos)
			{
				zos.SetLevel(9);
				zos.PutNextEntry(new ZipEntry("e.txt"));
				zos.Write(s_smallPayload, 0, s_smallPayload.Length);
				zos.CloseEntry();
			}
		}

		private static byte[] BuildPayload()
		{
			var sb = new StringBuilder(4 * 1024 * 1024 + 4096);
			var rnd = new Random(999);
			string[] words = { "package", "assembly", "version", "dependency", "framework", "netstandard",
				"target", "reference", "group", "metadata", "title", "authors", "description", "copyright",
				"tags", "namespace", "internal", "public", "class", "method", "return", "value" };
			while (sb.Length < 4 * 1024 * 1024)
			{
				sb.Append("  <dependency id=\"");
				for (int i = 0; i < 3; i++) { sb.Append(words[rnd.Next(words.Length)]); sb.Append('.'); }
				sb.Append("\" version=\"").Append(rnd.Next(1, 20)).Append('.').Append(rnd.Next(0, 99)).Append(".0\" />\n");
			}
			return Encoding.UTF8.GetBytes(sb.ToString());
		}

		private static string[] SynthesizeCorpus(string dir, int count)
		{
			Directory.CreateDirectory(dir);
			var rnd = new Random(12345);
			var paths = new List<string>(count);
			for (int i = 0; i < count; i++)
			{
				string p = Path.Combine(dir, $"synth.package.{i}.1.0.0.nupkg");
				paths.Add(p);
				if (File.Exists(p)) continue;
				using (var fs = File.Create(p))
				using (var arc = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
				{
					using (var w = new StreamWriter(arc.CreateEntry($"synth.package.{i}.nuspec",
						System.IO.Compression.CompressionLevel.Optimal).Open()))
						w.Write($"<?xml version=\"1.0\"?><package><metadata><id>synth.package.{i}</id></metadata></package>");
					int entries = 150 + rnd.Next(300);
					var payload = new byte[512];
					rnd.NextBytes(payload);
					for (int e = 0; e < entries; e++)
						using (var s = arc.CreateEntry($"lib/netstandard2.0/deep/namespace/path/segment{e}/Type{e}.xml",
							System.IO.Compression.CompressionLevel.Optimal).Open())
							s.Write(payload, 0, payload.Length);
				}
			}
			return paths.ToArray();
		}

		private sealed class CountingStream : Stream
		{
			public long Count { get; private set; }
			public override bool CanWrite => true;
			public override bool CanRead => false;
			public override bool CanSeek => false;
			public override long Length => Count;
			public override long Position { get => Count; set => throw new NotSupportedException(); }
			public override void Write(byte[] buffer, int offset, int count) => Count += count;
			public override void Flush() { }
			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
		}

		private static string GetOpt(string[] args, string name, string def)
		{
			for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
			return def;
		}
	}
}
