using System;
using System.Collections.Generic;
using System.IO;
using Telerik.JustDecompiler.Ast.Expressions;

namespace Differ.Providers
{
	public class AssemblyProvider : IAssemblyProvider
	{
		private readonly string _assemblyPath;

		public AssemblyProvider(string assemblyPath)
		{
			if (assemblyPath == null)
				throw new ArgumentNullException(nameof(assemblyPath));

			if (!File.Exists(assemblyPath))
				throw new FileNotFoundException($"no file found at {assemblyPath}");

			if (Path.GetExtension(assemblyPath) != ".dll")
				throw new Exception("file is not a dll");

			_assemblyPath = assemblyPath;
		}

		public IEnumerable<FileInfo> GetAssemblies(IEnumerable<string> targets)
		{
			yield return new FileInfo(_assemblyPath);
		}
	}

	public class AssemblyProviderFactory : IAssemblyProviderFactory
	{
		public string Name { get; } = "assembly";

		public string Format => $"{Name}|<assembly path>";

		public IAssemblyProvider Create(string[] command)
		{
			if (command.Length != 1)
				throw new Exception("command must have a length of 1");

			return new AssemblyProvider(command[0]);
		}
	}
}
