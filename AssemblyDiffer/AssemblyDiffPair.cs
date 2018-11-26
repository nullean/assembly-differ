using System;
using System.IO;
using JustAssembly.Core;

namespace Differ
{
	public class AssemblyDiffPair
	{
		public AssemblyDiffPair(FileInfo first, FileInfo second)
		{
			First = first ?? throw new ArgumentNullException(nameof(first));
			Second = second ?? throw new ArgumentNullException(nameof(second));
		}

		public FileInfo First { get; }
		public FileInfo Second { get; }
		public IDiffItem Diff { get; set; }
	}
}
