using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpZipLib.Zip;

using NuGet;

using NUnit.Framework;

namespace ICSharpCode.SharpZipLib.Tests.Zip
{
	[TestFixture]
	public class ZipPerformance
	{
		/// <summary>
		/// Gotten from the request of "orderby=PackageSize desc" from nuget (https://www.nuget.org/api/v2/Search()?$filter=IsLatestVersion&$orderby=PackageSize%20desc&$skip=0&$top=30&searchTerm=%27%27&targetFramework=%27%27&includePrerelease=false) , for testing on large zips.
		/// </summary>
		private static readonly PackageName[] LargePackageIds = {new PackageName("nalin", new SemanticVersion("1.0.0")), new PackageName("cryptopp", new SemanticVersion("5.6.3.4")), new PackageName("AWSSDKCPP-EC2", new SemanticVersion("0.13.20151001.8")), new PackageName("opencvcuda-debug.redist", new SemanticVersion("3.1.0")), new PackageName("opencvcuda-debug.symbols", new SemanticVersion("3.1.0")), new PackageName("Microsoft.Xbox.CloudCompute_110_md.SDK", new SemanticVersion("1502.26002")), new PackageName("opencvcuda-release.redist", new SemanticVersion("3.1.0")), new PackageName("opencvcuda-release.symbols", new SemanticVersion("3.1.0")), new PackageName("PayPal.Forms", new SemanticVersion("2.14.4")), new PackageName("Accusoft.ImageGear.PDF", new SemanticVersion("22.2.42")), new PackageName("Emgu2.9.0Beta", new SemanticVersion("2.9.2")), new PackageName("VDK.EmguCV.x64", new SemanticVersion("2.4.10.1")), new PackageName("Emgu_2.9.0_Beta_x64_1922", new SemanticVersion("1.0.0")), new PackageName("VVVV.EmguCV", new SemanticVersion("2.4.2.1")), new PackageName("Microsoft.Xbox.CloudCompute_110_mt.SDK", new SemanticVersion("1502.26004")), new PackageName("checkpushhuge", new SemanticVersion("9.0.1")), new PackageName("BigImage", new SemanticVersion("20.0.0")), new PackageName("Apitron.PDF.Kit", new SemanticVersion("1.0.38")), new PackageName("StrawberryPerl64", new SemanticVersion("5.22.2.1")), new PackageName("eMaxDataEssential", new SemanticVersion("3.0.0")), new PackageName("CEF3NativeDep", new SemanticVersion("3.2526.1366")), new PackageName("ZChat", new SemanticVersion("1.0.0")), new PackageName("Microsoft.Xbox.CloudCompute_120_mt.SDK", new SemanticVersion("1502.26003")), new PackageName("Microsoft.Xbox.CloudCompute_120_md.SDK", new SemanticVersion("1502.26002")), new PackageName("RadaeePDF_SDK_10", new SemanticVersion("1.9.10")), new PackageName("myEmguCV.Net", new SemanticVersion("0.0.12")), new PackageName("aarribillaga.architecture.web", new SemanticVersion("1.0.1")), new PackageName("Rust", new SemanticVersion("0.11.20140519")), new PackageName("VDK.EmguCV.x86", new SemanticVersion("2.4.10")), new PackageName("xcomponent.community", new SemanticVersion("4.5.0"))};

		[Test]
		public void OpenDirectoryMemStream()
		{
			DoReadDirectory(GetZipStreams().Cast<Stream>().ToList());
		}

		[Test]
		public void OpenDirectoryWrappedStream()
		{
			DoReadDirectory(GetZipStreams().Select(ms => new WrappedStream(ms)).Cast<Stream>().ToList());
		}

		private static void DoReadDirectory(List<Stream> zips)
		{
			/*
						for(int a = 0x100; a-- > 0;)
						{
							foreach(Stream stream in zips)
							{
								using(new ZipFile(stream) {IsStreamOwner = false})
								{
								}
							}
						}

			  */
//			MessageBox.Show("Before");
			IList<IDisposable> disposables = new List<IDisposable>();
			for(int a = 0x100; a-- > 0;)
			{
				foreach(Stream stream in zips)
				{
					disposables.Add(new ZipFile(stream) {IsStreamOwner = false});
				}
			}
//			MessageBox.Show("After");
			foreach(IDisposable disposable in disposables)
				disposable.Dispose();
		}

		private static List<MemoryStream> GetZipStreams()
		{
			var sourcerepo = new PriorityPackageRepository(MachineCache.Default, new PackageSourceProvider(Settings.LoadDefaultSettings(new PhysicalFileSystem("C:\\"), null, null)).CreateAggregateRepository(PackageRepositoryFactory.Default, true));

			// Just headers, no content dloaded yet (not much, at least)
			List<IPackage> ipackages = LargePackageIds.Select(pkgid => sourcerepo.FindPackage(pkgid.Id, pkgid.Version)).ToList();

			// Get the ZIPs
			List<MemoryStream> zips = ipackages.Select(ipkg =>
			{
				var mems = new MemoryStream();
				ipkg.GetStream().CopyTo(mems);
				return mems;
			}).ToList();
			return zips;
		}

		public class WrappedStream : Stream
		{
			private readonly Stream _core;

			public WrappedStream(Stream core)
			{
				_core = core;
			}

			public override bool CanRead => _core.CanRead;

			public override bool CanSeek => _core.CanSeek;

			public override bool CanWrite => _core.CanWrite;

			public override long Length => _core.Length;

			public override long Position
			{
				get
				{
					return _core.Position;
				}
				set
				{
					_core.Position = value;
				}
			}

			public override void Flush()
			{
				_core.Flush();
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				return _core.Read(buffer, offset, count);
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				return _core.Seek(offset, origin);
			}

			public override void SetLength(long value)
			{
				_core.SetLength(value);
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				_core.Write(buffer, offset, count);
			}
		}
	}
}