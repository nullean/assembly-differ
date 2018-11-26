using System;
using System.IO;
using System.Linq;
using ProcNet;

namespace Differ.Providers.GitHub
{
	public interface IGit
	{
		ProcessResult Execute(string workingDirectory, params string[] args);
	}

	public class Git : ExecutableBase, IGit
	{
		private readonly string _executable;
		private const string GitExe = "git.exe";

		public Git(string executable = null)
		{
			if (!string.IsNullOrEmpty(executable))
			{
				if (!File.Exists(executable))
					throw new FileNotFoundException($"{GitExe} does not exist at {executable}");

				_executable = executable;
			}
			else
				_executable = FindExecutable(GitExe);
		}


		public ProcessResult Execute(string workingDirectory, params string[] args)
		{
			var startArguments = new StartArguments(_executable, args)
			{
				WorkingDirectory = workingDirectory,
				WaitForStreamReadersTimeout = TimeSpan.FromSeconds(30)
			};

			var processResult = Proc.Start(startArguments);
			if (processResult.ExitCode == 0)
				return processResult;

			var lines = string.Join(Environment.NewLine, processResult.ConsoleOut.Select(c => c.Line));
			throw new Exception($"{GitExe} returned non-zero exit code: {lines}");
		}
	}
}
