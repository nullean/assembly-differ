using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Differ.Providers
{
	public class DirectoryAssemblyProvider : IAssemblyProvider
	{
		private readonly string _assemblyDirectory;

		public DirectoryAssemblyProvider(string assemblyDirectory)
		{
			if (assemblyDirectory == null)
				throw new ArgumentNullException(nameof(assemblyDirectory));

			if (!Directory.Exists(assemblyDirectory))
				throw new DirectoryNotFoundException($"No directory found at {assemblyDirectory}");

			_assemblyDirectory = assemblyDirectory;
		}

		public IEnumerable<FileInfo> GetAssemblies(IEnumerable<string> targets) =>
			Directory.EnumerateFiles(_assemblyDirectory, "*.dll")
				.Where(f => targets?.Contains(Path.GetFileNameWithoutExtension(f)) ?? true)
				.Select(f => new FileInfo(f));
	}

	public class DirectoryAssemblyProviderFactory : IAssemblyProviderFactory
	{
		public string Name { get; } = "directory";

		public string Format => $"{Name}|<directory path>";

		public IAssemblyProvider Create(string[] command)
		{
			if (command.Length != 1)
				throw new Exception("command must have a length of 1");

			return new DirectoryAssemblyProvider(command[0]);
		}
	}
}
