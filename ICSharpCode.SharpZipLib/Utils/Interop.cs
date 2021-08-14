using System.Runtime.InteropServices;

namespace ICSharpCode.SharpZipLib.Utils
{
	public static class Interop
	{
		private const string LibraryName = "libc";

		[DllImport(LibraryName, SetLastError = true)]
		public static extern int symlink(string target, string linkpath);
	}
}
