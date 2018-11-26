using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProcNet;

namespace Differ.Providers.GitHub
{
	public class GitHubAssemblyProviderFactory : IAssemblyProviderFactory
	{
		private readonly IGit _git;

		public GitHubAssemblyProviderFactory(IGit git) =>
			_git = git;

		public string Name { get; } = "github";

		public string Format => $"{Name}|<owner>/<repo>|<commit>|<build command>|<relative output path>";

		public IAssemblyProvider Create(string[] command) =>
			new GitHubAssemblyProvider(_git, command);
	}

	public class GitHubAssemblyProvider : IAssemblyProvider
	{
		private readonly IGit _git;
		private readonly GitHubDiffCommand _command;

		public GitHubAssemblyProvider(IGit git, string[] command)
		{
			_git = git;
			_command = new GitHubDiffCommand(command);
		}

		public IEnumerable<FileInfo> GetAssemblies(IEnumerable<string> targets)
		{
			var tempDir = Path.Combine(_command.TempDir, "differ", "github");
			if (!Directory.Exists(tempDir))
				Directory.CreateDirectory(tempDir);

			var repoDirectory = Path.GetFullPath(Path.Combine(tempDir, _command.Repo));
			var repoUri = $"git@github.com:{_command.Owner}/{_command.Repo}.git";

			ProcessResult result;

			if (!Directory.Exists(repoDirectory))
				// TODO: do something with stdout
				result = _git.Execute(tempDir, "clone", repoUri, _command.Repo);

			// TODO: do something with stdout
			result = _git.Execute(repoDirectory, "reset", "--hard");
			result = _git.Execute(repoDirectory, "checkout", _command.Commit);

			// compile
			var arguments = new StartArguments(_command.BuildCommand, _command.BuildArguments)
			{
				WorkingDirectory = repoDirectory,
				WaitForStreamReadersTimeout = TimeSpan.FromMinutes(1),
			};

			result = Proc.Start(arguments, TimeSpan.FromMinutes(10));

			var output = Path.GetFullPath(Path.Combine(repoDirectory, _command.Output));
			var isFile = false;
			var isDirectory = false;

			if (File.Exists(output))
				isFile = true;
			else if (Directory.Exists(output))
				isDirectory = true;

			if (!isFile && !isDirectory)
				throw new Exception($"file or directory not found at {output}");

			var commitDirectory = Path.Combine(tempDir, _command.Repo + "-" + _command.Commit);
			if (!Directory.Exists(commitDirectory))
				Directory.CreateDirectory(commitDirectory);

			if (isFile)
				File.Copy(output, Path.Combine(commitDirectory, Path.GetFileName(output)), true);
			else
			{
				foreach (var file in Directory.EnumerateFiles(output, "*.dll"))
				{
					var fileInfo = new FileInfo(file);
					File.Copy(fileInfo.FullName, Path.Combine(commitDirectory, fileInfo.Name), true);
				}
			}

			return Directory.EnumerateFiles(commitDirectory, "*.dll")
				.Where(f => targets?.Contains(Path.GetFileNameWithoutExtension(f)) ?? true)
				.Select(f => new FileInfo(f));
		}
	}
}
