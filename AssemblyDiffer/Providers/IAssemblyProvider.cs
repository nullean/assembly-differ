using System.Collections.Generic;
using System.IO;

namespace Differ.Providers
{
	public interface IAssemblyProvider
	{
		IEnumerable<FileInfo> GetAssemblies(HashSet<string> targets);
	}
}
