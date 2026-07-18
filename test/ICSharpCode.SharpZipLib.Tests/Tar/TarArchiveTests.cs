using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Tests.TestSupport;
using static ICSharpCode.SharpZipLib.Tests.TestSupport.Utils;
using NUnit.Framework;

namespace ICSharpCode.SharpZipLib.Tests.Tar
{
	[TestFixture]
	public class TarArchiveTests
	{
		[Test]
		[Category("Tar")]
		[Category("CreatesTempFile")]
		[TestCase("output")]
		[TestCase("output/")]
		[TestCase(@"output\", IncludePlatform = "Win")]
		public void ExtractingContentsWithNonTraversalPathSucceeds(string outputDir)
		{
			Assert.DoesNotThrow(() => ExtractTarOK(outputDir, "file", allowTraverse: false));
		}
		
		[Test]
		[Category("Tar")]
		[Category("CreatesTempFile")]
		public void ExtractingContentsWithExplicitlyAllowedTraversalPathSucceeds()
		{
			Assert.DoesNotThrow(() => ExtractTarOK("output", "../file", allowTraverse: true));
		}
		
		[Test]
		[Category("Tar")]
		[Category("CreatesTempFile")]
		[TestCase("output", "../file")]
		[TestCase("output/", "../file")]
		[TestCase("output", "../output.txt")]
		public void ExtractingContentsWithDisallowedPathsFails(string outputDir, string fileName)
		{
			Assert.Throws<InvalidNameException>(() => ExtractTarOK(outputDir, fileName, allowTraverse: false));
		}
		
		[Test]
		[Category("Tar")]
		[Category("CreatesTempFile")]
		[Platform(Include = "Win", Reason = "Backslashes are only treated as path separators on windows")]
		[TestCase(@"output\", @"..\file")]
		[TestCase(@"output/", @"..\file")]
		[TestCase("output", @"..\output.txt")]
		[TestCase(@"output\", @"..\output.txt")]
		public void ExtractingContentsOnWindowsWithDisallowedPathsFails(string outputDir, string fileName)
		{
			Assert.Throws<InvalidNameException>(() => ExtractTarOK(outputDir, fileName, allowTraverse: false));
		}
		
		public void ExtractTarOK(string outputDir, string fileName, bool allowTraverse)
		{
			var fileContent = Encoding.UTF8.GetBytes("file content");
			using var tempDir = GetTempDir();
			
			var tempPath = tempDir.FullName;
			var extractPath = Path.Combine(tempPath, outputDir);
			var expectedOutputFile = Path.Combine(extractPath, fileName);

			using var archiveStream = new MemoryStream();
			
			Directory.CreateDirectory(extractPath);

			using (var tos = new TarOutputStream(archiveStream, Encoding.UTF8){IsStreamOwner = false})
			{
				var entry = TarEntry.CreateTarEntry(fileName);
				entry.Size = fileContent.Length;
				tos.PutNextEntry(entry);
				tos.Write(fileContent, 0, fileContent.Length);
				tos.CloseEntry();
			}

			archiveStream.Position = 0;

			using (var ta = TarArchive.CreateInputTarArchive(archiveStream, Encoding.UTF8))
			{
				ta.ProgressMessageEvent += (archive, entry, message) 
					=> TestContext.WriteLine($"{entry.Name} {entry.Size} {message}");
				ta.ExtractContents(extractPath, allowTraverse);
			}

			Assert.That(File.Exists(expectedOutputFile));
		}

		[Test]
		[Category("Tar")]
		[Category("CreatesTempFile")]
		public void ExtractedEventIsRaisedOnlyForEntriesThatWereActuallyExtracted()
		{
			// A symlink entry that cannot be created (for example on Windows) must NOT raise the null-message
			// "extracted" event, otherwise consumers that read a null message as a successful extraction (such as
			// a file-count check) over-count entries that never reached disk. Contract: one terminal event per
			// entry, and a null message iff the entry now exists on disk.
			var fileContent = Encoding.UTF8.GetBytes("file content");
			using var tempDir = GetTempDir();
			var extractPath = tempDir.FullName;

			using var archiveStream = new MemoryStream();
			using (var tos = new TarOutputStream(archiveStream, Encoding.UTF8) { IsStreamOwner = false })
			{
				var fileEntry = TarEntry.CreateTarEntry("realfile");
				fileEntry.Size = fileContent.Length;
				tos.PutNextEntry(fileEntry);
				tos.Write(fileContent, 0, fileContent.Length);
				tos.CloseEntry();

				var linkEntry = TarEntry.CreateTarEntry("link");
				linkEntry.TarHeader.TypeFlag = TarHeader.LF_SYMLINK;
				linkEntry.TarHeader.LinkName = "realfile";
				linkEntry.Size = 0;
				tos.PutNextEntry(linkEntry);
				tos.CloseEntry();
			}

			archiveStream.Position = 0;

			var extractedEntries = new List<string>();
			using (var ta = TarArchive.CreateInputTarArchive(archiveStream, Encoding.UTF8))
			{
				ta.ProgressMessageEvent += (archive, entry, message) =>
				{
					if (message == null && !entry.IsDirectory)
						extractedEntries.Add(entry.Name);
				};
				ta.ExtractContents(extractPath);
			}

			// Every entry reported as extracted (null message) must actually be present on disk.
			foreach (var name in extractedEntries)
				Assert.That(File.Exists(Path.Combine(extractPath, name)), Is.True,
					$"Entry '{name}' raised the extracted event but is not present on disk");

			// The regular file is always extractable, so it must be reported.
			Assert.That(extractedEntries, NUnit.Framework.Does.Contain("realfile"));
		}

		// GNU tar emits LONGLINK ('K') immediately followed by LONGNAME ('L') for an entry whose name AND link
		// target both exceed 100 bytes (e.g. the dotnet SDK's versioned hardlinks/symlinks). The reader must
		// consume BOTH metadata headers and apply the long name/link to the real entry - never surface a second
		// '@LongLink' entry, which would otherwise be counted and extracted as a stray file and inflate file counts.
		// Fixture: a gzipped GNU tar with a 130-char-named file and a 130-char-named hardlink to it, i.e. the
		// blocks [L, file] then [K, L, hardlink].
		private const string KPlusLTarGzBase64 =
			"H4sICP/vW2oCA2tsdF9taW4udGFyAO3WSwoCMQwG4Kw9RU7QSV/p1r29hAsRESrMAzy+ZYRB3biyI5N8m5Ruw09+05lun2/" +
			"lnC/lCr9BFYcwz+pzkiO/vJ//KTEDZmhgGsZjjwhCjasDtfH9L/lnsinaeRLZl8w7sNE5TpwqIB9D8IDUMv/TcOoF7v++0wxIZv" +
			"71/kfAg95/vf9KZv61/zdRVqcZ3Pr+v/d/eu//7Mk7QNvi/Ajv/0opuR6vhdZ7ABYAAA==";

		[Test]
		[Category("Tar")]
		[Category("CreatesTempFile")]
		public void ConsecutiveGnuMetadataHeadersDoNotSurfaceBogusLongLinkEntry()
		{
			byte[] tarBytes;
			using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(KPlusLTarGzBase64)), System.IO.Compression.CompressionMode.Decompress))
			using (var tar = new MemoryStream())
			{
				gz.CopyTo(tar);
				tarBytes = tar.ToArray();
			}

			using var tempDir = GetTempDir();
			var extractPath = tempDir.FullName;

			var surfaced = new List<string>();
			using (var ta = TarArchive.CreateInputTarArchive(new MemoryStream(tarBytes), Encoding.UTF8))
			{
				ta.ProgressMessageEvent += (archive, entry, message) => { if (message == null && !entry.IsDirectory) surfaced.Add(entry.Name); };
				ta.ExtractContents(extractPath);
			}

			var onDisk = Directory.EnumerateFiles(extractPath, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();

			Assert.Multiple(() =>
			{
				Assert.That(surfaced.Any(n => n.Contains("@LongLink")), Is.False, "the @LongLink metadata header must not be surfaced as an entry");
				Assert.That(onDisk, Has.None.EqualTo("@LongLink"), "no stray @LongLink file must be written");
				Assert.That(surfaced.Any(n => n.Length >= 130), Is.True, "the long (130-char) name must be applied to the real entry");
			});
		}
	}
}
