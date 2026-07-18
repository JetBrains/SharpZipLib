using System.Collections.Generic;
using System.IO;
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
	}
}
