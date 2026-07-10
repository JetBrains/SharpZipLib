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
	// Standalone runner for the archive-open hot path (fast-scan-many-opens of nupkgs/OPCs).
	// Usage:
	//   dotnet run -c Release -f net8.0 -- --dir "C:\path\to\nupkgs" --scenario open  --iters 3
	//   dotnet run -c Release -f net48  -- --synth 200 --scenario nuspec --iters 5
	// Scenarios: open (central-directory scan only) | nuspec (open + read the .nuspec entry).
	internal static class Program
	{
		private static string s_arm = "sharpzip";        // sharpzip | pooled | system
		[ThreadStatic] private static byte[] s_readBuf;   // reused so the read buffer is not counted per-open
		private static long s_bytes;                      // total bytes actually (de)compressed (correctness check)
		private static int s_fails;                       // ops that threw (must be 0 for a valid run)
		private static byte[] s_payload;                  // compress scenario: representative input
		private static long s_compressedLen;              // compress scenario: last output size (ratio)

		private static int Main(string[] args)
		{
			string dir = GetOpt(args, "--dir", null);
			int synth = int.Parse(GetOpt(args, "--synth", "0"));
			int iters = int.Parse(GetOpt(args, "--iters", "3"));
			string scenario = GetOpt(args, "--scenario", "open");
			string arm = GetOpt(args, "--arm", "sharpzip"); // sharpzip | pooled | system
			s_arm = arm;

			// Default corpus: the local NuGet restore cache if present, else synthesize.
			if (dir == null && synth == 0)
			{
				string guess = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\JetBrains\~\NugetLocalRestore");
				if (Directory.Exists(guess)) dir = guess;
				else synth = 200;
			}

			string[] packages;
			Action<string> op;
			if (scenario == "compress")
			{
				s_payload = BuildPayload();
				packages = new string[25];          // path ignored; 25 compressions per iteration
				op = CompressPayload;
				Console.WriteLine($"compress payload: {s_payload.Length / 1024.0 / 1024.0:N2} MB representative text");
			}
			else
			{
				if (dir != null)
				{
					packages = Directory.GetFiles(dir, "*.nupkg", SearchOption.AllDirectories);
					Console.WriteLine($"corpus: {packages.Length} real nupkgs under {dir}");
				}
				else
				{
					string tmp = Path.Combine(Path.GetTempPath(), "ScanBench_synth");
					packages = SynthesizeCorpus(tmp, synth);
					Console.WriteLine($"corpus: {packages.Length} synthesized packages under {tmp}");
				}
				if (packages.Length == 0) { Console.Error.WriteLine("no packages found"); return 2; }
				op = scenario == "nuspec" ? (Action<string>)ReadNuspec : ScanOpen;
			}

			Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}  |  scenario={scenario} arm={arm} iters={iters}");

			// Warmup: JIT the path + warm the OS file cache (first touch of 10 GB is disk-bound).
			foreach (var p in packages) Safe(op, p);

			// Measure: single-threaded so per-thread allocation counting is exact.
			s_bytes = 0; s_fails = 0;
			long before = GC.GetAllocatedBytesForCurrentThread();
			var sw = Stopwatch.StartNew();
			long opens = 0;
			for (int it = 0; it < iters; it++)
				foreach (var p in packages) { Safe(op, p); opens++; }
			sw.Stop();
			long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

			double perOpenUs = sw.Elapsed.TotalMilliseconds * 1000.0 / opens;
			double perOpenKb = allocated / 1024.0 / opens;
			Console.WriteLine($"RESULT  opens={opens}  wall={sw.Elapsed.TotalMilliseconds:N0} ms  " +
				$"per-open={perOpenUs:N1} us  alloc/open={perOpenKb:N1} KB  totalAlloc={allocated / 1024.0 / 1024.0:N1} MB  " +
				$"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}  " +
				$"decompressed={s_bytes / 1024.0 / 1024.0:N1} MB fails={s_fails}");
			if (scenario == "compress")
			{
				Console.WriteLine($"COMPRESS  payload={s_payload.Length / 1024.0 / 1024.0:N2} MB  " +
					$"compressed={s_compressedLen / 1024.0:N0} KB  ratio={(double)s_compressedLen / s_payload.Length:P2}");
			}
			return 0;
		}

		// Scenario "compress": compress a fixed representative payload via the selected deflater arm.
		private static void CompressPayload(string _)
		{
			IDeflaterSource src =
				s_arm == "pooled" ? PooledDeflaterSource.Shared :
				s_arm == "system" ? SystemDeflaterSource.Default :
				DefaultDeflaterSource.Default;

			var counter = new CountingStream();
			using (var cs = src.CreateCompressor(counter, 9, leaveOpen: true))
			{
				cs.Write(s_payload, 0, s_payload.Length);
			}
			s_compressedLen = counter.Count;
			s_bytes += s_payload.Length;
		}

		// ~4 MB of deterministic nuspec-like text: compressible, but not trivially so.
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

		// Write-only stream that just counts bytes (measures compressed size without keeping it).
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

		// Scenario "open": scan the central directory only, touch each entry name. No decompression.
		private static void ScanOpen(string path)
		{
			using (var zf = new ZipFile(path))
			{
				foreach (ZipEntry e in zf) { var _ = e.Name; }
			}
		}

		// Scenario "nuspec": open + locate the root .nuspec + fully read it (one inflate).
		private static void ReadNuspec(string path)
		{
			using (var zf = new ZipFile(path))
			{
				switch (s_arm)
				{
					case "pooled": zf.InflaterSource = PooledInflaterSource.Shared; break;
					case "system": zf.InflaterSource = SystemInflaterSource.Default; break;
					// "sharpzip": leave the default managed Inflater
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

		private static void Safe(Action<string> op, string path)
		{
			try { op(path); } catch { s_fails++; /* skip malformed/edge packages; not the measurement subject */ }
		}

		// Emit a synthetic package set so the harness is self-contained / upstreamable.
		// Each package = a zip with many small entries + one root .nuspec (nupkg-shaped).
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
					// A nuspec at root.
					using (var w = new StreamWriter(arc.CreateEntry($"synth.package.{i}.nuspec",
						System.IO.Compression.CompressionLevel.Optimal).Open()))
						w.Write($"<?xml version=\"1.0\"?><package><metadata><id>synth.package.{i}</id><version>1.0.0</version></metadata></package>");
					// Many entries with OPC-style deep names to stress central-directory scanning.
					int entries = 150 + rnd.Next(300);
					var payload = new byte[512];
					rnd.NextBytes(payload);
					for (int e = 0; e < entries; e++)
					{
						var entry = arc.CreateEntry($"lib/netstandard2.0/deep/namespace/path/segment{e}/Type{e}.xml",
							System.IO.Compression.CompressionLevel.Optimal);
						using (var s = entry.Open()) s.Write(payload, 0, payload.Length);
					}
				}
			}
			return paths.ToArray();
		}

		private static string GetOpt(string[] args, string name, string def)
		{
			for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
			return def;
		}
	}
}
